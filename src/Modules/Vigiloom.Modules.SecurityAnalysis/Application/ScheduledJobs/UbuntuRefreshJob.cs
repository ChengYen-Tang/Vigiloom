using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class UbuntuRefreshJob(UbuntuRefresh refresh, ILogger<UbuntuRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == UbuntuRefreshResult.Failed) throw new InvalidOperationException("Ubuntu OSV refresh failed; see source status.");
        if (result == UbuntuRefreshResult.Conflict) logger.LogWarning("Ubuntu OSV refresh ended with {Result}", result);
    }
}

