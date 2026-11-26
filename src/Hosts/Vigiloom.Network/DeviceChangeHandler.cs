using System.Text.Json;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.NetworkMonitoring.Application;

namespace Vigiloom.Network;

/// <summary>Refreshes from the resource service so retries and out-of-order notifications converge on current data.</summary>
public sealed class DeviceChangeHandler(PingSettings settings, PingStore store) : IServiceWorkHandler
{
    public async Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        if (work.Kind != nameof(NetworkDeviceChanged)) throw new ArgumentException("Unsupported network work type.");
        var change = JsonSerializer.Deserialize<NetworkDeviceChanged>(work.Payload)
            ?? throw new ArgumentException("Device change is empty.");
        if (change.DeviceId.Version != 7 || change.Version is not { Length: 8 }) throw new ArgumentException("Invalid device change.");
        await settings.EnsureAsync(cancellationToken);
        await store.RefreshDeviceAsync(change.DeviceId, change.Version, await settings.GetAsync(cancellationToken), cancellationToken);
        return null;
    }
}
