using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Web.Diagnostics;

internal enum ObservationState { Unknown, Healthy, Unhealthy }
internal sealed record SchedulerObservation(ObservationState State, DateTimeOffset? LastHeartbeat, int Current, int Stale, int Unknown, bool Available);
internal sealed record PlatformStatusSnapshot(
    DateTimeOffset ObservedAt, ObservationState Database, SchedulerObservation Scheduler,
    WorkerDiagnosticsSnapshot? Workers, DateTimeOffset? LastAgentBatch, bool AgentQueryAvailable);

/// <summary>Combines independent, non-secret observations without equating liveness and health.</summary>
internal sealed class PlatformStatus(
    HealthCheckService checks, SchedulingHealth scheduling, WorkerDiagnosticsStore workers,
    AgentReceiverMonitoring receiver, TimeProvider clock, ILogger<PlatformStatus> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SchedulerStaleAfter = TimeSpan.FromMinutes(1);
    // Hangfire stores SQL datetime, which rounds to roughly 3 ms and can round ahead.
    private static readonly TimeSpan SqlDatetimeRoundingTolerance = TimeSpan.FromMilliseconds(4);

    public async Task<PlatformStatusSnapshot> GetAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var database = ObservationState.Unknown;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Timeout);
            var result = await checks.CheckHealthAsync(check => check.Name == "database", timeout.Token);
            database = result.Entries.TryGetValue("database", out var entry)
                ? entry.Status == HealthStatus.Healthy ? ObservationState.Healthy : ObservationState.Unhealthy
                : ObservationState.Unknown;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning("Database health unavailable: {ExceptionType}", exception.GetType().Name); }

        SchedulerObservation scheduler = new(ObservationState.Unknown, null, 0, 0, 0, false);
        WorkerDiagnosticsSnapshot? snapshots = null;
        DateTimeOffset? lastBatch = null;
        var receiverAvailable = false;
        if (database == ObservationState.Healthy)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);
                var reading = await scheduling.ReadAsync(timeout.Token);
                now = reading.ObservedAt;
                var current = reading.Values.Count(value => value is { } heartbeat && heartbeat <= now + SqlDatetimeRoundingTolerance && now - heartbeat <= SchedulerStaleAfter);
                var stale = reading.Values.Count(value => value is { } heartbeat && heartbeat <= now && now - heartbeat > SchedulerStaleAfter);
                var unknown = reading.Values.Count - current - stale;
                var latest = reading.Values.Where(value => value is not null).Max();
                var state = reading.Values.Count == 0 || unknown > 0 ? ObservationState.Unknown
                    : stale > 0 ? ObservationState.Unhealthy : ObservationState.Healthy;
                scheduler = new(state, latest, current, stale, unknown, true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning("Scheduler health unavailable: {ExceptionType}", exception.GetType().Name); }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);
                snapshots = await workers.GetSnapshotAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning("Worker diagnostics unavailable: {ExceptionType}", exception.GetType().Name); }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);
                lastBatch = await receiver.LastCommittedBatchAsync(timeout.Token);
                receiverAvailable = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning("Agent receiver observation unavailable: {ExceptionType}", exception.GetType().Name); }
        }
        return new(now, database, scheduler, snapshots, lastBatch, receiverAvailable);
    }
}
