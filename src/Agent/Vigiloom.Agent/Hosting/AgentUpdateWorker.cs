using System.Net.Sockets;
using System.Runtime.InteropServices;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Updates;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting;

/// <summary>Polls the desired Agent version and sends it to the local updater when configured.</summary>
internal sealed class AgentUpdateWorker(AgentVersionClient client, AgentSettingsRuntime settings, IConfiguration configuration,
    TimeProvider clock, ILogger<AgentUpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previousStatus = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var directory = configuration["Vigiloom:Agent:StateDirectory"];
            var server = configuration["Vigiloom:Agent:Server"];
            var socket = configuration["Vigiloom:Agent:UpdaterSocket"];
            var pipe = configuration["Vigiloom:Agent:UpdaterPipe"];
            var rid = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 && !string.IsNullOrWhiteSpace(socket)
                ? "linux-x64" : OperatingSystem.IsWindows() && pipe == WindowsUpdaterClient.PipeName
                    ? RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "win-x64", Architecture.X86 => "win-x86", _ => null }
                    : null;
            if (rid is not null && !string.IsNullOrWhiteSpace(directory) &&
                Uri.TryCreate(server, UriKind.Absolute, out var uri))
            {
                try
                {
                    var vault = new IdentityVault(directory);
                    var fetched = await client.GetAsync(vault, uri, stoppingToken);
                    var status = fetched.Status.ToString();
                    if (fetched is { Status: AgentVersionFetchStatus.Available } && fetched.Target?.RuntimeIdentifier == rid)
                    {
                        var handoff = await client.CreateHandoffAsync(vault, uri, fetched, stoppingToken);
                        // A changed runtime configuration must not send an old target or credential.
                        if (handoff is not null && directory == configuration["Vigiloom:Agent:StateDirectory"] &&
                            server == configuration["Vigiloom:Agent:Server"] && socket == configuration["Vigiloom:Agent:UpdaterSocket"] &&
                            pipe == configuration["Vigiloom:Agent:UpdaterPipe"])
                        {
                            if (OperatingSystem.IsLinux()) status = (await LinuxUpdaterClient.SubmitAsync(socket!, handoff, stoppingToken)).ToString();
                            else if (OperatingSystem.IsWindows()) status = (await WindowsUpdaterClient.SubmitAsync(handoff, stoppingToken)).ToString();
                        }
                        else status = "IdentityChanged";
                    }
                    else if (fetched.Status == AgentVersionFetchStatus.Available) status = "IncompatiblePlatform";
                    if (status != previousStatus)
                    {
                        logger.LogInformation("Agent update handoff status: {Status}.", status);
                        previousStatus = status;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or SocketException or UnauthorizedAccessException or ArgumentException or
                    InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or
                    System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    if (previousStatus != error.GetType().Name)
                    {
                        logger.LogWarning("Agent update handoff unavailable: {ErrorType}.", error.GetType().Name);
                        previousStatus = error.GetType().Name;
                    }
                }
            }
            var seconds = settings.Current.SettingsPollSeconds * (0.8 + Random.Shared.NextDouble() * 0.4);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
