using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class MsrcRefreshJob(MsrcRefresh refresh, ILogger<MsrcRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == MsrcRefreshResult.Failed) throw new InvalidOperationException("MSRC refresh failed; see source status.");
        if (result == MsrcRefreshResult.Conflict) logger.LogWarning("MSRC refresh ended with {Result}", result);
    }
}

