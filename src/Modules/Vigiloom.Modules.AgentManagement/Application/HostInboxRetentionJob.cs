using Hangfire;
using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed class HostInboxRetentionJob(HostInboxCleanup cleanup, HostLogIdentityCleanup identities, HostWorkRetention work,
    ILogger<HostInboxRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await cleanup.RunAsync(token);
        logger.LogInformation("Removed {Count} expired Agent receipts", count);
        var removedIds = await identities.RunAsync(token);
        logger.LogInformation("Removed {Count} obsolete host Log identities", removedIds);
        var removedWork = await work.RunAsync(token);
        logger.LogInformation("Removed {Count} completed Host work records", removedWork);
    }
}
