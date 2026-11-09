namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Cooperative pressure check; this does not impose an OS memory limit.</summary>
internal static class SamplingMemoryBudget
{
    public static bool IsExceeded(long workingSetBytes, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workingSetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        return workingSetBytes >= maximumBytes;
    }
}
