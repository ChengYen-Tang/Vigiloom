using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Observability;

internal sealed class ObservabilityMaintenanceSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        var options = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };
        jobs.AddOrUpdate("vigiloom-telemetry-retention", Job.FromExpression<ObservabilityRetentionJob>(job => job.RunAsync(CancellationToken.None), "observability"), Cron.Hourly(), options);
        jobs.AddOrUpdate("vigiloom-metric-rollup", Job.FromExpression<ObservabilityMetricRollupJob>(job => job.RunAsync(CancellationToken.None), "observability"), Cron.Minutely(), options);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
