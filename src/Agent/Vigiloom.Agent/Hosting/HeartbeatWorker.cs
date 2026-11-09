using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Metrics;

namespace Vigiloom.Agent.Hosting;

/// <summary>Runs the initial heartbeat schedule; configuration synchronization will supply its effective interval.</summary>
internal sealed class HeartbeatWorker(AgentHeartbeatClient client, AgentSettingsRuntime settings, AgentQueueCapacityRuntime capacity, AgentMetricSettingsRuntime metrics, IConfiguration configuration, TimeProvider clock, ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(AgentSettings.Default.HeartbeatSeconds), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            string status;
            try
            {
                var directory = configuration["Vigiloom:Agent:StateDirectory"];
                var server = configuration["Vigiloom:Agent:Server"];
                if (string.IsNullOrWhiteSpace(directory) || !Uri.TryCreate(server, UriKind.Absolute, out var uri))
                {
                    settings.Bind(null);
                    capacity.Bind(null);
                    metrics.Bind(null);
                    status = "NotConfigured";
                }
                else
                {
                    var vault = new IdentityVault(directory);
                    var owner = await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken);
                    if (!string.Equals(directory, configuration["Vigiloom:Agent:StateDirectory"], StringComparison.Ordinal) ||
                        !string.Equals(server, configuration["Vigiloom:Agent:Server"], StringComparison.Ordinal))
                    {
                        settings.Bind(null);
                        capacity.Bind(null);
                        metrics.Bind(null);
                        status = "ConfigurationChanged";
                    }
                    else
                    {
                        settings.Bind(owner);
                        capacity.Bind(owner);
                        metrics.Bind(owner);
                        var initialPeriod = TimeSpan.FromSeconds(settings.Current.HeartbeatSeconds);
                        if (timer.Period != initialPeriod) timer.Period = initialPeriod;
                        status = (await client.SendAsync(vault, uri, settings.Report(), capacity.Report(), stoppingToken)).ToString();
                    }
                }
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
            var period = TimeSpan.FromSeconds(settings.Current.HeartbeatSeconds);
            if (timer.Period != period) timer.Period = period;
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
