using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

namespace Vigiloom.Worker.Hosting;

public sealed class BaselineRefreshJob(BaselineRefresh refresh, ILogger<BaselineRefreshJob> logger, IConfiguration configuration)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        if (SecurityCutover.IsEnabled(configuration)) return;
        foreach (var key in BaselineRefresh.SourceKeys)
        {
            var result = await refresh.RunDueAsync(key, token);
            if (result is BaselineRefreshResult.Failed or BaselineRefreshResult.Conflict)
                logger.LogWarning("Baseline source {SourceKey} refresh ended with {Result}", key, result);
        }
    }
}

internal sealed class BaselineRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-baseline-refresh", Job.FromExpression<BaselineRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
