using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

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
