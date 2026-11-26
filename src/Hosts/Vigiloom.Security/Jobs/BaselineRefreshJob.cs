using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

namespace Vigiloom.Security.Jobs;

internal sealed class BaselineRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-security-baseline-refresh", Job.FromExpression<BaselineRefreshJob>(job => job.RunAsync(CancellationToken.None), "security"),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
