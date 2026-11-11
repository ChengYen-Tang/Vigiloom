using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Worker.Hosting;

public sealed class InventoryExpiryJob(InventoryExpiry expiry, ILogger<InventoryExpiryJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await expiry.RunAsync(token);
        if (count > 0) logger.LogInformation("Expired {Count} incomplete inventory snapshots", count);
    }
}

internal sealed class InventoryExpirySchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-inventory-expiry", Job.FromExpression<InventoryExpiryJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
