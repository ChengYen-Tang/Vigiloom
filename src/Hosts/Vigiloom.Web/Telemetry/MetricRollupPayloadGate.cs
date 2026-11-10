namespace Vigiloom.Web.Telemetry;

/// <summary>Bounds simultaneous in-memory OTLP rollup downloads.</summary>
internal sealed class MetricRollupPayloadGate : IDisposable
{
    public MetricRollupPayloadGate() : this(TimeSpan.FromMinutes(2), CancellationToken.None) { }
    internal MetricRollupPayloadGate(TimeSpan deadline, CancellationToken deadlineSignal)
    {
        Deadline = deadline;
        DeadlineSignal = deadlineSignal;
    }
    internal SemaphoreSlim Gate { get; } = new(4, 4);
    internal TimeSpan Deadline { get; }
    internal CancellationToken DeadlineSignal { get; }
    public void Dispose() => Gate.Dispose();
}
