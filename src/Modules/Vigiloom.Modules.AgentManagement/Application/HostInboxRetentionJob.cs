using Hangfire;
using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed class HostInboxRetentionJob(HostInboxCleanup cleanup, ILogger<HostInboxRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await cleanup.RunAsync(token);
        logger.LogInformation("Removed {Count} expired Agent receipts", count);
    }
}
