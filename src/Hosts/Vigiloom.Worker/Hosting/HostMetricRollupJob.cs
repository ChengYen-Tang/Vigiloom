using Hangfire;
using Hangfire.Common;
using System.Diagnostics;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;

namespace Vigiloom.Worker.Hosting;

public sealed class HostMetricRollupJob(HostMetricRollupBackfill backfill)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public async Task RunAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(45))
            if (await backfill.RunBatchAsync(token) < HostMetricRollupBackfill.BatchSize) break;
    }
}

internal sealed class HostMetricRollupSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-host-metric-rollup", Job.FromExpression<HostMetricRollupJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
