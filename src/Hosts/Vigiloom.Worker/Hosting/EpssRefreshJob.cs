using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Worker.Hosting;

public sealed class EpssRefreshJob(EpssRefresh refresh, ILogger<EpssRefreshJob> logger, IConfiguration configuration)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        if (SecurityCutover.IsEnabled(configuration)) return;
        var result = await refresh.RunDueAsync(token);
        if (result == EpssRefreshResult.Failed) throw new InvalidOperationException("EPSS refresh failed; see source status.");
        if (result == EpssRefreshResult.Conflict) logger.LogWarning("EPSS refresh ended with {Result}", result);
    }
}

internal sealed class EpssRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-first-epss-refresh", Job.FromExpression<EpssRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
