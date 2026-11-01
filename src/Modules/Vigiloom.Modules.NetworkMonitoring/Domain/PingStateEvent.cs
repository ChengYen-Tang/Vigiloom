using Vigiloom.Contracts.Network;

namespace Vigiloom.Modules.NetworkMonitoring.Domain;

/// <summary>A durable state transition for future alert consumption; not a delivered notification.</summary>
public sealed class PingStateEvent
{
    public long RowId { get; private set; }
    public Guid DeviceId { get; set; }
    public string Target { get; set; } = null!;
    public long Generation { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Reachability Previous { get; set; }
    public Reachability Current { get; set; }
}
