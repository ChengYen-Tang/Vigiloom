using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Worker.Hosting;

public sealed class MsrcRefreshJob(MsrcRefresh refresh, ILogger<MsrcRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == MsrcRefreshResult.Failed) throw new InvalidOperationException("MSRC refresh failed; see source status.");
        if (result == MsrcRefreshResult.Conflict) logger.LogWarning("MSRC refresh ended with {Result}", result);
    }
}

internal sealed class MsrcRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-msrc-cvrf-refresh", Job.FromExpression<MsrcRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
