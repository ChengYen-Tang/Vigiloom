namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>An administrator-defined association, never inferred from telemetry attributes.</summary>
public sealed class CollectorRoute
{
    private CollectorRoute() { }

    public CollectorRoute(Guid collectorId, Guid sourceId, string exporter)
    {
        if (collectorId.Version != 7 || sourceId.Version != 7) throw new ArgumentException("Collector and source are required.");
        if (string.IsNullOrWhiteSpace(exporter) || exporter.Length > 128) throw new ArgumentException("An exporter name of at most 128 characters is required.", nameof(exporter));
        CollectorId = collectorId; SourceId = sourceId; Exporter = exporter;
    }

    public Guid CollectorId { get; private set; }
    public Guid SourceId { get; private set; }
    public string Exporter { get; private set; } = "";
    public byte[] ExporterBytes { get; private set; } = [];
}
