using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class BaselineRefreshJob(BaselineRefresh refresh, ILogger<BaselineRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        foreach (var key in BaselineRefresh.SourceKeys)
        {
            var result = await refresh.RunDueAsync(key, token);
            if (result is BaselineRefreshResult.Failed or BaselineRefreshResult.Conflict)
                logger.LogWarning("Baseline source {SourceKey} refresh ended with {Result}", key, result);
        }
    }
}

