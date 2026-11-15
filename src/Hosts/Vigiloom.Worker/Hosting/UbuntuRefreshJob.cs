using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Worker.Hosting;

public sealed class UbuntuRefreshJob(UbuntuRefresh refresh, ILogger<UbuntuRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == UbuntuRefreshResult.Failed) throw new InvalidOperationException("Ubuntu OSV refresh failed; see source status.");
        if (result == UbuntuRefreshResult.Conflict) logger.LogWarning("Ubuntu OSV refresh ended with {Result}", result);
    }
}

internal sealed class UbuntuRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-ubuntu-osv-refresh", Job.FromExpression<UbuntuRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
