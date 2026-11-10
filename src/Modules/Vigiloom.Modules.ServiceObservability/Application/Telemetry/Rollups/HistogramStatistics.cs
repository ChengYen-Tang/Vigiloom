namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Checks necessary consistency conditions without treating incomplete statistics as proof.</summary>
internal static class HistogramStatistics
{
    internal static bool Valid(ulong count, bool hasSum, double sum, bool hasMin, double min, bool hasMax, double max,
        out bool sumBoundsUnverified)
    {
        sumBoundsUnverified = false;
        if (hasSum && !double.IsFinite(sum) || hasMin && !double.IsFinite(min) || hasMax && !double.IsFinite(max) ||
            hasMin && hasMax && min > max || count == 0 && (hasMin || hasMax || hasSum && sum != 0)) return false;
        if (count == 1 && (hasMin && hasMax && min != max || hasSum && hasMin && sum != min ||
            hasSum && hasMax && sum != max)) return false;
        if (count == 2 && hasSum && hasMin && hasMax && sum != min + max) return false;
        if (count > 1 && hasSum)
        {
            // Upstream floating-point sums can contain rounding; this is a necessary bound,
            // not proof that a claimed population was actually observed.
            var factor = ((double)count + 8) * 2.220446049250313e-16;
            if (!hasMin || !hasMax || factor >= 0.25 || count > 2) sumBoundsUnverified = true;
            if (hasMin && hasMax && factor < 0.25)
            {
                var mean = sum / count;
                var scale = Math.Max(Math.Abs(mean), Math.Max(Math.Abs(min), Math.Abs(max)));
                var slack = scale * factor / (1 - factor) + double.Epsilon * Math.Min((double)count, 1_000_000);
                if (mean < min - slack || mean > max + slack) return false;
            }
        }
        return true;
    }
}
