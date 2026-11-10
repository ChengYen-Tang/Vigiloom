namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Five-minute UTC windows in the original OTLP nanosecond time domain.</summary>
internal readonly record struct MetricWindow(ulong Start, ulong End)
{
    private const ulong Width = 300_000_000_000;

    internal static bool TryForSample(ulong timestamp, out MetricWindow window)
    {
        if (timestamp == 0) { window = default; return false; }
        return TryCreate(timestamp, out window);
    }

    // An OTLP delta represents (start, end], so an endpoint at a boundary belongs to the prior window.
    internal static bool TryForDeltaEnd(ulong timestamp, out MetricWindow window)
    {
        if (timestamp == 0) { window = default; return false; }
        return TryCreate(timestamp - 1, out window);
    }

    private static bool TryCreate(ulong timestamp, out MetricWindow window)
    {
        var start = timestamp - timestamp % Width;
        if (start > ulong.MaxValue - Width) { window = default; return false; }
        window = new(start, start + Width);
        return true;
    }
}
