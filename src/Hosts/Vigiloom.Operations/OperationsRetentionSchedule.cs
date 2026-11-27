using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AlertingOperations.Application.Retention;

namespace Vigiloom.Operations;

internal sealed class OperationsRetentionSchedule(IRecurringJobManager jobs, IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.RemoveIfExists("vigiloom-line-delivery");
        jobs.RemoveIfExists("vigiloom-agent-package-refresh");
        if (!string.IsNullOrWhiteSpace(configuration["Services:Web:Url"]))
            jobs.RemoveIfExists("vigiloom-settings-audit-retention");
        jobs.AddOrUpdate("vigiloom-operations-retention",
            Job.FromExpression<OperationsRetentionJob>(job => job.RunAsync(CancellationToken.None), "operations"),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
