using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

namespace Vigiloom.Worker.Hosting;

public sealed class AdvisoryRefreshJob(AdvisoryRefresh refresh, ILogger<AdvisoryRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == AdvisoryRefreshResult.Failed)
            throw new InvalidOperationException("Advisory refresh failed; its staged import requires attention.");
        if (result == AdvisoryRefreshResult.Conflict)
            logger.LogWarning("Advisory refresh ended with {Result}", result);
    }
}

internal sealed class AdvisoryRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-advisory-refresh", Job.FromExpression<AdvisoryRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
