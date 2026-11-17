using System.ComponentModel;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Samples this Agent process; CPU is relative to one logical processor.</summary>
internal sealed class AgentSelfMetrics(IMetricResourceMonitor resources, TimeProvider clock)
{
    private TimeSpan? previousCpu;
    private long previousTimestamp;

    public AgentRuntimeReport? Read(AgentRuntimeReport? identity)
    {
        if (identity is null) return null;
        try
        {
            var cpu = resources.ReadCpuTime();
            var memory = resources.ReadWorkingSetBytes();
            var timestamp = clock.GetTimestamp();
            var elapsed = previousCpu is null ? TimeSpan.Zero : clock.GetElapsedTime(previousTimestamp, timestamp);
            double? percent = previousCpu is { } previous && cpu >= previous && elapsed > TimeSpan.Zero
                ? (cpu - previous).TotalSeconds / elapsed.TotalSeconds * 100 : null;
            previousCpu = cpu;
            previousTimestamp = timestamp;
            return identity with { CpuPercent = percent, WorkingSetBytes = memory >= 0 ? memory : null };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            previousCpu = null;
            return identity;
        }
    }
}
