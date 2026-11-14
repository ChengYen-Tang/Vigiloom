using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Web.Publishing;

internal sealed class PublicationPreviewReader(DeviceManagement devices, PingStore ping, HealthStatusReader health,
    PublicationAdministration settings)
{
    public async Task<PublicationPreview?> ReadAsync(PublicationResourceKind kind, Guid id, CancellationToken token)
    {
        if (await settings.ReadAsync(kind, id, token) is null) return null;
        if (kind == PublicationResourceKind.Device)
        {
            var device = await devices.GetAsync(id, token);
            if (device is null || device.Archived) return null;
            if (!device.Enabled) return new("Unknown", null);
            var status = (await ping.GetStatusAsync(id, [device.Address], device.Version, token)).Targets[0];
            return new(status.State switch { Reachability.Reachable => "Operational", Reachability.Unreachable => "Unavailable", _ => "Unknown" },
                status.State == Reachability.Reachable && status.LastOutcome == PingOutcome.Success && !status.Stale ? status.LatencyMilliseconds : null, status.MeasuredAt);
        }
        if (kind == PublicationResourceKind.Service)
        {
            var result = await health.ReadAsync(id, token);
            return result is null ? null : new(result.State switch
            { ServiceHealthState.Healthy => "Healthy", ServiceHealthState.Degraded => "Degraded", ServiceHealthState.Unhealthy => "Unhealthy", _ => "Unknown" }, null);
        }
        return new("Group", null);
    }
}
