using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;

namespace Vigiloom.Worker.Hosting;

public sealed class HostMetricRetentionJob(HostMetricRetentionCleanup cleanup, ILogger<HostMetricRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public async Task RunAsync(CancellationToken token)
    {
        var (samples, rollups) = await cleanup.RunAsync(token);
        logger.LogInformation("Host metric retention removed {SampleCount} samples and {RollupCount} rollups", samples, rollups);
    }
}

internal sealed class HostMetricRetentionSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-host-metric-retention", Job.FromExpression<HostMetricRetentionJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
