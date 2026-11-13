using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

namespace Vigiloom.Worker.Hosting;

public sealed class PingRollupBackfillJob(PingRollupBackfill backfill)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        for (var batch = 0; batch < 20; batch++)
            if (await backfill.RunBatchAsync(token) < PingRollupBackfill.BatchSize) break;
    }
}

internal sealed class PingRollupBackfillSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-ping-rollup-backfill", Job.FromExpression<PingRollupBackfillJob>(job => job.RunAsync(CancellationToken.None)),
            "* * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
