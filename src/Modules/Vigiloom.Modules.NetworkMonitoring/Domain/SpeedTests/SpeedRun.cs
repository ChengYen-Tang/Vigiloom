using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.SpeedTests;
namespace Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;

// A cancellable measurement job with its own detail URL: Resource, not a high-volume sample.
public sealed class SpeedRun
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public string Location { get; set; } = "central";
    public Guid RequestId { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Automatic { get; set; }
    public SpeedTestState State { get; set; }
    public int MaxMiB { get; set; }
    public int TimeoutSeconds { get; set; }
    public string Provider { get; set; } = "M-Lab NDT7 / ndt7-client-go 0.10.1";
    public string? Server { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Isp { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? DownloadMbps { get; set; }
    public double? UploadMbps { get; set; }
    public double? LatencyMilliseconds { get; set; }
    public long? Bytes { get; set; }
    public string? DiagnosticCode { get; set; }
    public bool CancellationRequested { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public byte[] Version { get; private set; } = [];
}
