namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

/// <summary>Resource and instrumentation scope shared by one received span group.</summary>
public sealed class TraceContext
{
    public long RowId { get; set; }
    public Guid ServiceId { get; set; }
    public required byte[] ResourcePayload { get; set; }
    public required byte[] ScopePayload { get; set; }
    public required string ResourceSchemaUrl { get; set; }
    public required string ScopeSchemaUrl { get; set; }
}
