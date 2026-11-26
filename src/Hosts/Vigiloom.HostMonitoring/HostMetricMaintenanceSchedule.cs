using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;

namespace Vigiloom.HostMonitoring;

internal sealed class HostMetricMaintenanceSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-host-service-metric-rollup", Job.FromExpression<HostMetricMaintenance>(job => job.RollupAsync(CancellationToken.None), "host"),
            Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        jobs.AddOrUpdate("vigiloom-host-service-metric-retention", Job.FromExpression<HostMetricMaintenance>(job => job.RetainAsync(CancellationToken.None), "host"),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
