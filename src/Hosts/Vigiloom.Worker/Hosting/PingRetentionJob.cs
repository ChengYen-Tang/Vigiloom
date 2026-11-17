using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

namespace Vigiloom.Worker.Hosting;

public sealed class PingRetentionJob(PingRetentionCleanup cleanup, ILogger<PingRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public async Task RunAsync(CancellationToken token)
    {
        var (samples, rollups) = await cleanup.RunAsync(token);
        logger.LogInformation("Ping retention removed {SampleCount} samples and {RollupCount} rollups", samples, rollups);
    }
}

internal sealed class PingRetentionSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-ping-retention", Job.FromExpression<PingRetentionJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
