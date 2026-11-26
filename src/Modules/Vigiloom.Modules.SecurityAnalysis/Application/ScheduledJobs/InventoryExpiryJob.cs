using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class InventoryExpiryJob(InventoryExpiry expiry, ILogger<InventoryExpiryJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await expiry.RunAsync(token);
        if (count > 0) logger.LogInformation("Expired {Count} incomplete inventory snapshots", count);
    }
}
