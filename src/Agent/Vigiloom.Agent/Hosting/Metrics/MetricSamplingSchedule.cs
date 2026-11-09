using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Independent monotonic schedules; caller serializes sampling and records starts.</summary>
internal sealed class MetricSamplingSchedule(TimeProvider clock)
{
    private long? lastHost;
    private long? lastProcess;

    public MetricSamplingDue GetDue(AgentMetricSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid()) throw new ArgumentException("Invalid metric settings.", nameof(settings));
        var now = clock.GetTimestamp();
        var hostWait = Remaining(lastHost, now, TimeSpan.FromSeconds(settings.HostIntervalSeconds));
        var processWait = Remaining(lastProcess, now, TimeSpan.FromSeconds(settings.ProcessIntervalSeconds));
        return new(hostWait == TimeSpan.Zero, processWait == TimeSpan.Zero, hostWait < processWait ? hostWait : processWait);
    }

    public void MarkStarted(bool host, bool process)
    {
        var now = clock.GetTimestamp();
        if (host) lastHost = now;
        if (process) lastProcess = now;
    }

    private TimeSpan Remaining(long? last, long now, TimeSpan interval)
    {
        if (last is null) return TimeSpan.Zero;
        var elapsed = clock.GetElapsedTime(last.Value, now);
        if (elapsed < TimeSpan.Zero) return TimeSpan.Zero;
        return elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
    }
}

internal readonly record struct MetricSamplingDue(bool Host, bool Process, TimeSpan Delay);
