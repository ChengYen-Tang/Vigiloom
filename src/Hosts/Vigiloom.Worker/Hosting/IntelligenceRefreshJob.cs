using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Worker.Hosting;

public sealed class IntelligenceRefreshJob(IntelligenceRefresh refresh, ILogger<IntelligenceRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == IntelligenceRefreshResult.Failed)
            throw new InvalidOperationException("Intelligence source refresh failed; see source status for details.");
        if (result == IntelligenceRefreshResult.Conflict)
            logger.LogWarning("Intelligence source refresh ended with {Result}", result);
    }
}

internal sealed class IntelligenceRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-intelligence-refresh", Job.FromExpression<IntelligenceRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
