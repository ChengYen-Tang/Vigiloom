using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class SignatureRefreshJob(SignatureRefresh refresh, ILogger<SignatureRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == SignatureRefreshResult.Failed) throw new InvalidOperationException("Signature-Base source refresh failed; see source status.");
        if (result == SignatureRefreshResult.Conflict) logger.LogWarning("Signature-Base source refresh ended with {Result}", result);
    }
}

