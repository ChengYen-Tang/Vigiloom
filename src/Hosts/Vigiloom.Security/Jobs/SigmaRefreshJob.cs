using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

namespace Vigiloom.Security.Jobs;

internal sealed class SigmaRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-security-sigma-refresh", Job.FromExpression<SigmaRefreshJob>(job => job.RunAsync(CancellationToken.None), "security"),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
