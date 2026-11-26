using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Network;

/// <summary>Periodically sends the latest transient scanner state to Operations.</summary>
internal sealed class NetworkDiagnosticsPublisher(NetworkDiagnostics diagnostics, IHttpClientFactory clients,
    ILogger<NetworkDiagnosticsPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                var (ping, speed) = diagnostics.Snapshot();
                using var response = await clients.CreateClient("diagnostics").PostAsJsonAsync("internal/v1/diagnostics/workers",
                    new NetworkDiagnosticUpdate(diagnostics.BootId, ping, speed), stoppingToken);
                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Could not report network diagnostics: {ExceptionType}", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
