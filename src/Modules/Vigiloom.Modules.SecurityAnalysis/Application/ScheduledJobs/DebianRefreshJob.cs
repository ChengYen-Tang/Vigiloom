using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class DebianRefreshJob(DebianRefresh refresh, ILogger<DebianRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == DebianRefreshResult.Failed) throw new InvalidOperationException("Debian source refresh failed; see source status.");
        if (result == DebianRefreshResult.Conflict) logger.LogWarning("Debian source refresh ended with {Result}", result);
    }
}

