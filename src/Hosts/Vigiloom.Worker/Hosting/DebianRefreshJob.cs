using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Worker.Hosting;

public sealed class DebianRefreshJob(DebianRefresh refresh, ILogger<DebianRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == DebianRefreshResult.Failed) throw new InvalidOperationException("Debian source refresh failed; see source status.");
        if (result == DebianRefreshResult.Conflict) logger.LogWarning("Debian source refresh ended with {Result}", result);
    }
}

internal sealed class DebianRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-debian-refresh", Job.FromExpression<DebianRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
