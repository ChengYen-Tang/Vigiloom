using Hangfire;
using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed class NetworkAutomaticSpeedJob(SpeedQueue queue)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token) => await queue.RequestAsync(ResourceIds.Create(), token);
}
