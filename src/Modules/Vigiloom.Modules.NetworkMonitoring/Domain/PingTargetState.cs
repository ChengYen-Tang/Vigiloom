using Vigiloom.Contracts.Network;

namespace Vigiloom.Modules.NetworkMonitoring.Domain;

public sealed class PingTargetState
{
    public Guid DeviceId { get; set; }
    public string Target { get; set; } = null!;
    public byte[] DeviceVersion { get; set; } = [];
    public bool Enabled { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long Generation { get; set; }
    public Reachability State { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? MeasuredAt { get; set; }
    public PingOutcome? Outcome { get; set; }
    public double? LatencyMilliseconds { get; set; }
    public string? ResolvedAddress { get; set; }
    public string Location { get; set; } = "central";
}
