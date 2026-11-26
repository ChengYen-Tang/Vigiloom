using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class AdvisoryRefreshJob(AdvisoryRefresh refresh, ILogger<AdvisoryRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == AdvisoryRefreshResult.Failed)
            throw new InvalidOperationException("Advisory refresh failed; its staged import requires attention.");
        if (result == AdvisoryRefreshResult.Conflict)
            logger.LogWarning("Advisory refresh ended with {Result}", result);
    }
}
