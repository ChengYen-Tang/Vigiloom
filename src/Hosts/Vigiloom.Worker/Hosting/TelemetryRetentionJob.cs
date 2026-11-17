using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

namespace Vigiloom.Worker.Hosting;

public sealed class TelemetryRetentionJob(TelemetryRetentionCleanup cleanup, ILogger<TelemetryRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public async Task RunAsync(CancellationToken token)
    {
        var (logs, spans) = await cleanup.RunAsync(token);
        var (pruned, expired) = await cleanup.RunMetricsAsync(token);
        var (observations, intervals) = await cleanup.RunHealthAsync(token);
        logger.LogInformation("Application telemetry retention removed {LogCount} logs, {SpanCount} spans and {ExpiredCount} metric windows; pruned {PrunedCount} metric windows", logs, spans, expired, pruned);
        logger.LogInformation("Service health retention removed {ObservationCount} observations and {IntervalCount} intervals", observations, intervals);
    }
}

internal sealed class TelemetryRetentionSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-telemetry-retention", Job.FromExpression<TelemetryRetentionJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
