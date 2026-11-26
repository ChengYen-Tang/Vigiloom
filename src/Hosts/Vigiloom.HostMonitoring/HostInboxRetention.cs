using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal sealed class HostInboxRetentionSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-host-inbox-retention",
            Job.FromExpression<HostInboxRetentionJob>(job => job.RunAsync(CancellationToken.None), "host"),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
