using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;

namespace Vigiloom.Agent.Hosting;

/// <summary>Runs the initial heartbeat schedule; configuration synchronization will supply its effective interval.</summary>
internal sealed class HeartbeatWorker(AgentHeartbeatClient client, IConfiguration configuration, TimeProvider clock, ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            string status;
            try
            {
                var directory = configuration["Vigiloom:Agent:StateDirectory"];
                var server = configuration["Vigiloom:Agent:Server"];
                status = string.IsNullOrWhiteSpace(directory) || !Uri.TryCreate(server, UriKind.Absolute, out var uri)
                    ? "NotConfigured"
                    : (await client.SendAsync(new IdentityVault(directory), uri, stoppingToken)).ToString();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
            {
                status = ex.GetType().Name;
            }
            if (!string.Equals(previous, status, StringComparison.Ordinal))
            {
                if (status == nameof(HeartbeatResult.Accepted)) logger.LogInformation("Agent heartbeat connected.");
                else logger.LogWarning("Agent heartbeat unavailable: {Status}.", status);
                previous = status;
            }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
