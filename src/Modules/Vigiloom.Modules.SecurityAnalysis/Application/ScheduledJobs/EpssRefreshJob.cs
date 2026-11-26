using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class EpssRefreshJob(EpssRefresh refresh, ILogger<EpssRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == EpssRefreshResult.Failed) throw new InvalidOperationException("EPSS refresh failed; see source status.");
        if (result == EpssRefreshResult.Conflict) logger.LogWarning("EPSS refresh ended with {Result}", result);
    }
}
