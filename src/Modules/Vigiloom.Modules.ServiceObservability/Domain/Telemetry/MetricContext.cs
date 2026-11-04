namespace Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

/// <summary>Shared OTLP resource, scope and metric metadata for one received metric group.</summary>
public sealed class MetricContext
{
    public long RowId { get; set; }
    public Guid ServiceId { get; set; }
    public required byte[] ResourcePayload { get; set; }
    public required byte[] ScopePayload { get; set; }
    public required byte[] MetricPayload { get; set; }
    // Null means the name was too long for the optional lookup index or predates it;
    // the original name remains intact in MetricPayload.
    public string? MetricName { get; set; }
    public byte[]? MetricNameBytes { get; private set; }
    public required string ResourceSchemaUrl { get; set; }
    public required string ScopeSchemaUrl { get; set; }
}
