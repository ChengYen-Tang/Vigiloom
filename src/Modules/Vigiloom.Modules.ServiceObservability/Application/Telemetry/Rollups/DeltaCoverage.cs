namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Tracks whether sorted OTLP delta intervals cover a window without overlap.</summary>
internal struct DeltaCoverage
{
    private readonly ulong start;
    private readonly ulong end;
    private ulong cursor;

    internal DeltaCoverage(ulong start, ulong end) => (this.start, this.end, cursor) = (start, end, start);
    internal bool Gap { get; private set; }
    internal bool Overlap { get; private set; }
    internal bool CrossesBoundary { get; private set; }

    internal bool Observe(ulong intervalStart, ulong intervalEnd)
    {
        if (intervalStart < start) { CrossesBoundary = true; return false; }
        if (intervalStart > cursor) Gap = true;
        if (intervalStart < cursor) Overlap = true;
        cursor = Math.Max(cursor, intervalEnd);
        return true;
    }

    internal void Complete()
    {
        if (cursor < end) Gap = true;
    }
}
