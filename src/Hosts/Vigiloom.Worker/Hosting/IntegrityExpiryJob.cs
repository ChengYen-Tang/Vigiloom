using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

namespace Vigiloom.Worker.Hosting;

public sealed class IntegrityExpiryJob(IntegrityExpiry expiry, ILogger<IntegrityExpiryJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await expiry.RunAsync(token);
        if (count > 0) logger.LogInformation("Closed or released {Count} integrity scans", count);
    }
}

internal sealed class IntegrityExpirySchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-integrity-expiry", Job.FromExpression<IntegrityExpiryJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
