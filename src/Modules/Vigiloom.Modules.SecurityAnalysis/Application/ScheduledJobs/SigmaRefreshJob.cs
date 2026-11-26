using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class SigmaRefreshJob(SigmaRefresh refresh, ILogger<SigmaRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == SigmaRefreshResult.Failed) throw new InvalidOperationException("Sigma source refresh failed; see source status.");
        if (result == SigmaRefreshResult.Conflict) logger.LogWarning("Sigma source refresh ended with {Result}", result);
    }
}

