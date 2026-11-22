using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;
using Vigiloom.Updater.Core.Transport;
using Vigiloom.Updater.Core.Updates;
using NuGet.Versioning;

namespace Vigiloom.Updater.Hosting;

/// <summary>Owns recovery and serial update processing for one installed Agent.</summary>
internal sealed class RecoveryWorker(IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<RecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = configuration["Vigiloom:Updater:InstallationRoot"];
        var stateDirectory = configuration["Vigiloom:Updater:StateDirectory"];
        if (!IsAbsoluteDirectory(root) || !IsAbsoluteDirectory(stateDirectory))
        {
            logger.LogError("Updater installation and state directories must both be absolute, non-root paths.");
            lifetime.StopApplication();
            return;
        }
        IAgentUpdateRuntime runtime;
        if (OperatingSystem.IsWindows()) runtime = new WindowsAgentRuntime(root!);
        else if (OperatingSystem.IsLinux()) runtime = new SystemdAgentRuntime(root!);
        else { logger.LogError("Updater is not supported on this operating system."); lifetime.StopApplication(); return; }

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
            lifetime.StopApplication();
            return;
        }
        await using (ownership)
        {
            var journal = new UpdateJournal(stateDirectory!);
            var coordinator = new UpdateCoordinator(journal, runtime);
            if (!await RecoverAsync(coordinator, runtime, root!, stoppingToken))
            {
                if (!stoppingToken.IsCancellationRequested) lifetime.StopApplication();
                return;
            }
            if (!TryGetConfiguration(stateDirectory!, out var options))
            {
                logger.LogInformation("Updater handoff is inactive until installation settings are configured.");
                lifetime.StopApplication();
                return;
            }

            try
            {
                using var http = new HttpClient(new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                }) { Timeout = Timeout.InfiniteTimeSpan };
                var clock = TimeProvider.System;
                var delivery = new UpdateResultDelivery(http, journal, clock);
                var credentialStore = new MachineCredentialStore(stateDirectory!);
                var central = new CentralAssignmentClient(http, clock);
                using var serial = new SemaphoreSlim(1, 1);
                async Task<bool> StageCredentialAsync(UpdateHandoff request, CancellationToken token)
                {
                    await serial.WaitAsync(token);
                    try
                    {
                        var status = await central.GetCurrentAsync(options!.Server, options.DeviceId, request.Credential, token);
                        if (status.Status == CurrentAssignmentStatus.Unauthorized)
                            status = await central.GetCurrentAsync(options.Server, options.DeviceId, request.PendingCredential!, token);
                        if (status.Status is not (CurrentAssignmentStatus.Available or CurrentAssignmentStatus.Changed)) return false;
                        await credentialStore.StageAsync(request.Credential, request.PendingCredential!, token);
                        return true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        logger.LogWarning("Updater could not stage credential rotation: {ErrorType}.", error.GetType().Name);
                        return false;
                    }
                    finally { serial.Release(); }
                }
                await using var listener = CreateListener(options!, StageCredentialAsync);
                var processor = new UpdateRequestProcessor(central,
                    new CentralPackageAcquirer(options!.Server, options.TrustDirectory, options.CacheDirectory,
                        4L * 1024 * 1024 * 1024, clock),
                    new UpdateTransaction(http, journal, runtime, clock), journal, runtime, options.Server,
                    options.DeviceId, root!, options.RuntimeIdentifier, options.UpdaterVersion,
                    delivery, credentialStore.WriteAsync);
                await StartPendingReplacementAsync(journal, runtime, root!, stateDirectory!, options.UpdaterVersion, stoppingToken);
                async Task HandleAsync(UpdateHandoff request, CancellationToken token)
                {
                    await serial.WaitAsync(token);
                    try
                    {
                        try
                        {
                            var result = await processor.ProcessAsync(request, token);
                            logger.LogInformation("Updater request result: {Assignment}, {Phase}.", result.Assignment, result.State?.Phase);
                        }
                        finally
                        {
                            await RecoverAndDeliverAsync(request, recoveryToken => RecoverAsync(coordinator, runtime, root!, recoveryToken),
                                delivery, options.Server, logger, token);
                            await StartPendingReplacementAsync(journal, runtime, root!, stateDirectory!, options.UpdaterVersion, token);
                        }
                    }
                    finally { serial.Release(); }
                }

                using var pollStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var polling = PollAsync(credentialStore, central, options.Server, options.DeviceId, HandleAsync, logger, pollStop.Token);
                try
                {
                    await RunListenerAsync(listener, HandleAsync, (_, _) => Task.FromResult(true), logger, stoppingToken);
                }
                finally
                {
                    await pollStop.CancelAsync();
                    await polling;
                }
                if (!stoppingToken.IsCancellationRequested) lifetime.StopApplication();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogError("Updater listener stopped: {ErrorType}.", error.GetType().Name);
                lifetime.StopApplication();
            }
        }
    }

    private async Task StartPendingReplacementAsync(UpdateJournal journal, IAgentUpdateRuntime runtime, string root,
        string stateDirectory, string runningVersion, CancellationToken token)
    {
        var state = await journal.ReadAsync(token);
        if (state is not { Phase: UpdatePhase.Succeeded }) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var selected = Path.GetFullPath(await runtime.GetSelectedDirectoryAsync(token));
        var target = Path.GetFullPath(state.TargetDirectory);
        var staging = Path.GetFullPath(Path.Combine(root, "staging"));
        if (!string.Equals(selected, target, comparison) || !string.Equals(Path.GetDirectoryName(target), staging, comparison) ||
            !Guid.TryParseExact(Path.GetFileName(target), "N", out var targetId) || targetId.Version != 7)
            throw new InvalidDataException("The pending Updater does not belong to the selected Agent.");
        var source = Path.Combine(target, "updater");
        if (state.PendingUpdaterDirectory is null)
        {
            if (Directory.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(source, recursive: true);
            return;
        }
        if (!string.Equals(Path.GetFullPath(state.PendingUpdaterDirectory), source, comparison))
            throw new InvalidDataException("The pending Updater does not belong to the selected Agent.");
        var candidate = InstalledAgentVersion.ReadUpdater(source);
        if (!NuGetVersion.TryParse(runningVersion, out var running) || !NuGetVersion.TryParse(candidate, out var replacement))
            throw new InvalidDataException("The Updater version is invalid.");
        if (running >= replacement)
        {
            await journal.WriteAsync(state with { PendingUpdaterDirectory = null }, token);
            if (Directory.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(source, recursive: true);
            return;
        }
        var statusPath = Path.Combine(stateDirectory, "updater-replacement.status");
        var status = File.Exists(statusPath) ? await File.ReadAllTextAsync(statusPath, token) : null;
        if (status is not null && status.Trim() is var recorded &&
            (recorded == $"{state.Id:N} started" || recorded.StartsWith($"{state.Id:N} failed", StringComparison.Ordinal)))
        {
            logger.LogWarning("Updater replacement remains {Status}; inspect the local replacement status.", recorded.StartsWith($"{state.Id:N} failed", StringComparison.Ordinal) ? "failed" : "in progress");
            return;
        }
        await UpdaterReplacementLauncher.LaunchAsync(state, root, stateDirectory, token);
        logger.LogInformation("Updater replacement was handed to the operating system.");
    }

    internal static async Task PollAsync(MachineCredentialStore credentials, CentralAssignmentClient central, Uri server, Guid deviceId,
        Func<UpdateHandoff, CancellationToken, Task> handle, ILogger logger, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    var saved = await credentials.ReadCandidatesAsync(token);
                    if (saved is null) continue;
                    var credential = saved.Current;
                    var current = await central.GetCurrentAsync(server, deviceId, credential, token);
                    if (current.Status == CurrentAssignmentStatus.Unauthorized && saved.Pending is not null)
                    {
                        credential = saved.Pending;
                        current = await central.GetCurrentAsync(server, deviceId, credential, token);
                    }
                    if (current.Status == CurrentAssignmentStatus.Available && current.Target is { } target)
                        await handle(new UpdateHandoff { UpdateId = target.UpdateId, Credential = credential }, token);
                    else if (current.Status == CurrentAssignmentStatus.Changed)
                        await handle(new UpdateHandoff { SyncOnly = true, Credential = credential }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { logger.LogWarning("Updater polling failed: {ErrorType}.", error.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<bool> RecoverAsync(UpdateCoordinator coordinator, IAgentUpdateRuntime runtime, string root, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var state = await coordinator.RecoverAsync(token);
                if (state is null) return true;
                try
                {
                    PreparedPackageCleanup.RemoveRolledBack(state, root);
                    PreparedPackageCleanup.RemoveRetainedFailures(state, root);
                    if (state.Phase == UpdatePhase.Succeeded && state.RetainedFailedDirectories is { Count: > 0 })
                    {
                        state = state with { RetainedFailedDirectories = null };
                        await new UpdateJournal(stateDirectory: configuration["Vigiloom:Updater:StateDirectory"]!).WriteAsync(state, token);
                    }
                    if (state.Phase == UpdatePhase.Succeeded)
                        PreparedPackageCleanup.RemoveSuperseded(state, root, await runtime.GetSelectedDirectoryAsync(token));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // Disk cleanup is not required to keep the current Agent running.
                    logger.LogWarning("Updater could not remove old package files: {ErrorType}.", error.GetType().Name);
                }
                logger.LogInformation("Updater recovery state: {Phase}.", state.Phase);
                return true;
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

    internal static async Task<bool> RecoverAndDeliverAsync(UpdateHandoff request, Func<CancellationToken, Task<bool>> recover,
        UpdateResultDelivery delivery, Uri server, ILogger logger, CancellationToken token)
    {
        var recovered = await recover(token);
        try { await delivery.FlushAsync(server, request.Credential, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Updater could not deliver recovery results: {ErrorType}.", error.GetType().Name);
        }
        return recovered;
    }

    internal static async Task RunListenerAsync(ILocalUpdateListener listener,
        Func<UpdateHandoff, CancellationToken, Task> process, Func<UpdateHandoff, CancellationToken, Task<bool>> recover,
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
                if (!await recover(request, stoppingToken)) break;
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

    private static ILocalUpdateListener CreateListener(Settings settings, Func<UpdateHandoff, CancellationToken, Task<bool>> stageCredential)
    {
        if (OperatingSystem.IsWindows()) return new WindowsUpdateListener(new SecurityIdentifier(settings.AgentSid!), stageCredential);
        if (OperatingSystem.IsLinux()) return LinuxUpdateListener.Create(settings.SocketDirectory!,
            uint.Parse(settings.AgentUid!), uint.Parse(settings.AgentGroupId!), stageCredential);
        throw new PlatformNotSupportedException();
    }

    private static bool IsAbsoluteDirectory(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) && !string.Equals(Path.GetFullPath(path), Path.GetPathRoot(path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record Settings(Uri Server, Guid DeviceId, string RuntimeIdentifier, string UpdaterVersion,
        string TrustDirectory, string CacheDirectory, string? SocketDirectory, string? AgentSid, string? AgentUid, string? AgentGroupId);
}
