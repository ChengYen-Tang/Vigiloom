using Vigiloom.Contracts.Network;

namespace Vigiloom.Modules.NetworkMonitoring.Domain;

public sealed class PingSample
{
    private PingSample() { }
    public PingSample(Guid deviceId, string location, DateTimeOffset occurredAt, DateTimeOffset receivedAt, bool succeeded, double? latencyMilliseconds)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Device identifier must not be empty.", nameof(deviceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (succeeded != latencyMilliseconds.HasValue || (latencyMilliseconds.HasValue && (!double.IsFinite(latencyMilliseconds.Value) || latencyMilliseconds.Value < 0)))
            throw new ArgumentException("Successful measurements require a non-negative latency; failed measurements have no latency.", nameof(latencyMilliseconds));
        DeviceId = deviceId;
        Location = location;
        OccurredAt = occurredAt.ToUniversalTime();
        ReceivedAt = receivedAt.ToUniversalTime();
        Succeeded = succeeded;
        LatencyMilliseconds = latencyMilliseconds;
    }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string Location { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public bool Succeeded { get; private set; }
    public double? LatencyMilliseconds { get; private set; }
    public string? Target { get; private set; }
    public string? ResolvedAddress { get; private set; }
    public long? Generation { get; private set; }
    public PingOutcome? Outcome { get; private set; }
    public bool Aggregated { get; private set; }

    internal void MarkAggregated() => Aggregated = true;

    public PingSample(Guid deviceId, string target, string? resolvedAddress, string location, long generation,
        DateTimeOffset occurredAt, DateTimeOffset receivedAt, PingOutcome outcome, double? latencyMilliseconds)
        : this(deviceId, location, occurredAt, receivedAt, outcome == PingOutcome.Success, latencyMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Target = target;
        ResolvedAddress = resolvedAddress;
        Generation = generation;
        Outcome = outcome;
    }
}
