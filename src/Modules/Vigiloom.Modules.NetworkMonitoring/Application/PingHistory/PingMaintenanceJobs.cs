using Hangfire;
using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

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

public sealed class PingRollupBackfillJob(PingRollupBackfill backfill)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        for (var batch = 0; batch < 20; batch++)
            if (await backfill.RunBatchAsync(token) < PingRollupBackfill.BatchSize) break;
    }
}
