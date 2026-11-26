using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class IntegrityExpiryJob(IntegrityExpiry expiry, ILogger<IntegrityExpiryJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await expiry.RunAsync(token);
        if (count > 0) logger.LogInformation("Closed or released {Count} integrity scans", count);
    }
}
