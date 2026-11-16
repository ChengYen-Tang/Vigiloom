using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;
using Vigiloom.Updater.Core.Transport;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Hosting;

/// <summary>Owns recovery and serial update processing for one installed Agent.</summary>
internal sealed class RecoveryWorker(IConfiguration configuration, ILogger<RecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = configuration["Vigiloom:Updater:InstallationRoot"];
        var stateDirectory = configuration["Vigiloom:Updater:StateDirectory"];
        if (!IsAbsoluteDirectory(root) || !IsAbsoluteDirectory(stateDirectory))
        {
            logger.LogInformation("Updater is inactive until installation and state directories are configured.");
            return;
        }
        IAgentUpdateRuntime runtime;
        if (OperatingSystem.IsWindows()) runtime = new WindowsAgentRuntime(root!);
        else if (OperatingSystem.IsLinux()) runtime = new SystemdAgentRuntime(root!);
        else { logger.LogError("Updater is not supported on this operating system."); return; }

        FileStream ownership;
        try
        {
            // The installer protects this directory. Hold the lock through recovery and all updates;
            // it is tied to the installation, even if a second process has a different state path.
            ownership = new FileStream(Path.Combine(root!, "updater.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Updater cannot acquire installation ownership: {ErrorType}.", error.GetType().Name);
            return;
        }
        await using (ownership)
        {
            var journal = new UpdateJournal(stateDirectory!);
            var coordinator = new UpdateCoordinator(journal, runtime);
            if (!await RecoverAsync(coordinator, root!, stoppingToken)) return;
            if (!TryGetConfiguration(stateDirectory!, out var options))
            {
                logger.LogInformation("Updater handoff is inactive until installation settings are configured.");
                return;
            }

            try
            {
                await using var listener = CreateListener(options!);
                using var http = new HttpClient(new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                }) { Timeout = Timeout.InfiniteTimeSpan };
                var clock = TimeProvider.System;
                var processor = new UpdateRequestProcessor(new CentralAssignmentClient(http, clock),
                    new CentralPackageAcquirer(options!.Server, options.TrustDirectory, options.CacheDirectory,
                        4L * 1024 * 1024 * 1024, clock),
                    new UpdateTransaction(http, journal, runtime, clock), journal, runtime, options.Server,
                    options.DeviceId, root!, options.RuntimeIdentifier, options.UpdaterVersion);
                await RunListenerAsync(listener, async (request, token) =>
                {
                    var result = await processor.ProcessAsync(request, token);
                    logger.LogInformation("Updater request result: {Assignment}, {Phase}.", result.Assignment, result.State?.Phase);
                }, token => RecoverAsync(coordinator, root!, token), logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogError("Updater listener stopped: {ErrorType}.", error.GetType().Name);
            }
        }
    }

    private async Task<bool> RecoverAsync(UpdateCoordinator coordinator, string root, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var state = await coordinator.RecoverAsync(token);
                if (state is null) return true;
                PreparedPackageCleanup.RemoveRolledBack(state, root);
                logger.LogInformation("Updater recovery state: {Phase}.", state.Phase);
                if (state.Phase == UpdatePhase.RollbackFailed)
                {
                    logger.LogError("Updater recovery failed; new updates are disabled until the installation is repaired.");
                    return false;
                }
                if (state.Phase != UpdatePhase.RollingBack) return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (Exception error)
            {
                logger.LogError("Updater could not read or recover its journal: {ErrorType}.", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
        return false;
    }

    internal static async Task RunListenerAsync(ILocalUpdateListener listener,
        Func<UpdateHandoff, CancellationToken, Task> process, Func<CancellationToken, Task<bool>> recover,
        ILogger logger, CancellationToken stoppingToken)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var accepting = listener.RunAsync(shutdown.Token);
        try
        {
            await foreach (var request in listener.Requests.ReadAllAsync(stoppingToken))
            {
                try { await process(request, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error) { logger.LogWarning("Updater request failed: {ErrorType}.", error.GetType().Name); }
                // Recover immediately, even when the stopped Agent cannot send another request.
                if (!await recover(stoppingToken)) break;
            }
        }
        finally
        {
            await shutdown.CancelAsync();
            try { await accepting; }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }
    }

    private bool TryGetConfiguration(string stateDirectory, out Settings? settings)
    {
        settings = null;
        var baseUrl = configuration["Vigiloom:Updater:Server"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps ||
            server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0 ||
            !Guid.TryParse(configuration["Vigiloom:Updater:DeviceId"], out var deviceId) || deviceId.Version != 7)
            return false;
        var rid = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" :
            OperatingSystem.IsWindows() ? RuntimeInformation.ProcessArchitecture switch
            { Architecture.X64 => "win-x64", Architecture.X86 => "win-x86", _ => null } : null;
        var version = typeof(UpdaterHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        var trust = Path.Combine(stateDirectory, "trust");
        if (rid is null || string.IsNullOrWhiteSpace(version) || !File.Exists(Path.Combine(trust, "trust.sqlite"))) return false;
        var socketDirectory = configuration["Vigiloom:Updater:IpcDirectory"];
        var agentSid = configuration["Vigiloom:Updater:AgentSid"];
        if (OperatingSystem.IsLinux() && (!IsAbsoluteDirectory(socketDirectory) ||
            !uint.TryParse(configuration["Vigiloom:Updater:AgentUid"], out _) ||
            !uint.TryParse(configuration["Vigiloom:Updater:AgentGroupId"], out _))) return false;
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(agentSid)) return false;
        settings = new(server, deviceId, rid, version, trust, Path.Combine(stateDirectory, "cache"), socketDirectory, agentSid,
            configuration["Vigiloom:Updater:AgentUid"], configuration["Vigiloom:Updater:AgentGroupId"]);
        return true;
    }

    private static ILocalUpdateListener CreateListener(Settings settings)
    {
        if (OperatingSystem.IsWindows()) return new WindowsUpdateListener(new SecurityIdentifier(settings.AgentSid!));
        if (OperatingSystem.IsLinux()) return LinuxUpdateListener.Create(settings.SocketDirectory!,
            uint.Parse(settings.AgentUid!), uint.Parse(settings.AgentGroupId!));
        throw new PlatformNotSupportedException();
    }

    private static bool IsAbsoluteDirectory(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) && !string.Equals(Path.GetFullPath(path), Path.GetPathRoot(path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record Settings(Uri Server, Guid DeviceId, string RuntimeIdentifier, string UpdaterVersion,
        string TrustDirectory, string CacheDirectory, string? SocketDirectory, string? AgentSid, string? AgentUid, string? AgentGroupId);
}
