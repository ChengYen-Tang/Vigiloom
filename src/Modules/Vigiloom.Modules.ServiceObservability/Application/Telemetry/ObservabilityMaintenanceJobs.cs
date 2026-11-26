using System.Net.Http.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Persistence;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

public sealed class ObservabilityRetentionJob(TelemetryRetentionCleanup cleanup,
    IDbContextFactory<ServiceObservabilityDbContext> contexts, IHttpClientFactory clients,
    ILogger<ObservabilityRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public async Task RunAsync(CancellationToken token)
    {
        await cleanup.RunAsync(token);
        await cleanup.RunMetricsAsync(token);
        await cleanup.RunHealthAsync(token);
        await cleanup.RunSecurityDetectionsAsync(token);
        var client = clients.CreateClient("operations");
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            using var response = await client.PostAsJsonAsync("internal/v1/timeline/trace-cleanup", new ApplicationLogCleanupRequest(policy.TraceDays), token);
            response.EnsureSuccessStatusCode();
            var removed = await response.Content.ReadFromJsonAsync<int>(cancellationToken: token);
            await tx.CommitAsync(token);
            if (removed < 500) break;
        }
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            using var response = await client.PostAsJsonAsync("internal/v1/timeline/health-cleanup", new ApplicationLogCleanupRequest(policy.HealthDays), token);
            response.EnsureSuccessStatusCode();
            var removed = await response.Content.ReadFromJsonAsync<int>(cancellationToken: token);
            await tx.CommitAsync(token);
            if (removed < 500) break;
        }
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            using var response = await client.PostAsJsonAsync("internal/v1/application-logs/cleanup", new ApplicationLogCleanupRequest(policy.LogDays), token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ApplicationLogCleanupResult>(cancellationToken: token)
                ?? throw new InvalidOperationException("Operations returned an empty cleanup result.");
            await tx.CommitAsync(token);
            logger.LogInformation("Removed {Count} expired application logs", result.Logs);
            if (!result.HasMore) break;
        }
        var legacy = clients.CreateClient("legacy-web");
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            using var response = await legacy.PostAsJsonAsync("internal/v1/legacy-host-logs/cleanup", new ApplicationLogCleanupRequest(policy.LogDays), token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<HostLogCleanupBatch>(cancellationToken: token)
                ?? throw new InvalidOperationException("Web returned an empty host Log cleanup result.");
            await tx.CommitAsync(token);
            if (!result.HasMore) break;
        }
    }
}

public sealed class ObservabilityMetricRollupJob(MetricRollupCoordinator coordinator)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public Task RunAsync(CancellationToken token) => coordinator.RunAsync(token);
}
