namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

/// <summary>Shared resource and instrumentation scope for a received OTLP group.</summary>
public sealed class LogContext
{
    public long RowId { get; set; }
    public Guid ServiceId { get; set; }
    public required byte[] ResourcePayload { get; set; }
    public required byte[] ScopePayload { get; set; }
    public required string ResourceSchemaUrl { get; set; }
    public required string ScopeSchemaUrl { get; set; }
}
