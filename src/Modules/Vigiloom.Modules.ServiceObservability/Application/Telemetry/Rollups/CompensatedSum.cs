namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Reduces floating-point cancellation during addition; callers must still reject non-finite results.</summary>
internal static class CompensatedSum
{
    internal static void Add(ref double sum, ref double correction, double value)
    {
        var next = sum + value;
        correction += Math.Abs(sum) >= Math.Abs(value) ? sum - next + value : value - next + sum;
        sum = next;
    }
}
