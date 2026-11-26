using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

namespace Vigiloom.Worker.Hosting;

public sealed class SigmaRefreshJob(SigmaRefresh refresh, ILogger<SigmaRefreshJob> logger, IConfiguration configuration)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        if (SecurityCutover.IsEnabled(configuration)) return;
        var result = await refresh.RunDueAsync(token);
        if (result == SigmaRefreshResult.Failed) throw new InvalidOperationException("Sigma source refresh failed; see source status.");
        if (result == SigmaRefreshResult.Conflict) logger.LogWarning("Sigma source refresh ended with {Result}", result);
    }
}

internal sealed class SigmaRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-sigma-refresh", Job.FromExpression<SigmaRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
