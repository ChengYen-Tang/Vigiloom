using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

namespace Vigiloom.Security.Jobs;

internal sealed class SignatureRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-security-signature-refresh", Job.FromExpression<SignatureRefreshJob>(job => job.RunAsync(CancellationToken.None), "security"),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
