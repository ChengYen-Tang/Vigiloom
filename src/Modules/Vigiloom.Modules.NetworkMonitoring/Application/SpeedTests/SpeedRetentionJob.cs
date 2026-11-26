using Hangfire;
using Microsoft.Extensions.Logging;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed class NetworkSpeedRetentionJob(SpeedRetentionCleanup cleanup, ILogger<NetworkSpeedRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await cleanup.RunAsync(token);
        logger.LogInformation("Speed retention cleanup removed {Count} expired results", count);
    }
}
