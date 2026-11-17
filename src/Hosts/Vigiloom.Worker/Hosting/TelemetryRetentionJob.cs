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
        logger.LogInformation("Application telemetry retention removed {LogCount} logs and {SpanCount} spans", logs, spans);
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
