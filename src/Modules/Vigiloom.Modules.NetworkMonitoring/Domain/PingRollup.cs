using Vigiloom.Contracts.Network;

namespace Vigiloom.Modules.NetworkMonitoring.Domain;

/// <summary>Five-minute statistics for one device, configured target and measurement location.</summary>
public sealed class PingRollup
{
    private PingRollup() { }
    internal PingRollup(Guid deviceId, string? target, string location, DateTimeOffset start)
        => (DeviceId, Target, Location, WindowStart) = (deviceId, target, location, start);

    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string? Target { get; private set; }
    public string Location { get; private set; } = null!;
    public DateTimeOffset WindowStart { get; private set; }
    public long Attempts { get; private set; }
    public long Successes { get; private set; }
    public long Timeouts { get; private set; }
    public long Unreachable { get; private set; }
    public long DnsErrors { get; private set; }
    public long PermissionDenied { get; private set; }
    public long MonitorErrors { get; private set; }
    public long UnknownFailures { get; private set; }
    public double LatencySum { get; private set; }
    public bool LatencyOverflow { get; private set; }
    public double? LatencyMin { get; private set; }
    public double? LatencyMax { get; private set; }
    public DateTimeOffset FirstOccurredAt { get; private set; }
    public DateTimeOffset LastOccurredAt { get; private set; }

    internal void Add(PingSample sample)
    {
        if (Attempts == 0) FirstOccurredAt = LastOccurredAt = sample.OccurredAt;
        else { if (sample.OccurredAt < FirstOccurredAt) FirstOccurredAt = sample.OccurredAt; if (sample.OccurredAt > LastOccurredAt) LastOccurredAt = sample.OccurredAt; }
        Attempts++;
        if (sample.Succeeded)
        {
            var value = sample.LatencyMilliseconds!.Value;
            Successes++;
            var sum = LatencySum + value;
            if (double.IsFinite(sum)) LatencySum = sum;
            else LatencyOverflow = true;
            LatencyMin = LatencyMin is null ? value : Math.Min(LatencyMin.Value, value);
            LatencyMax = LatencyMax is null ? value : Math.Max(LatencyMax.Value, value);
        }
        else switch (sample.Outcome)
        {
            case PingOutcome.Timeout: Timeouts++; break;
            case PingOutcome.Unreachable: Unreachable++; break;
            case PingOutcome.DnsError: DnsErrors++; break;
            case PingOutcome.PermissionDenied: PermissionDenied++; break;
            case PingOutcome.MonitorError: MonitorErrors++; break;
            default: UnknownFailures++; break;
        }
    }
}
