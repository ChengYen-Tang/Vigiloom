namespace Vigiloom.Modules.ServiceObservability.Domain.Health;

/// <summary>An interval of one target's evaluated state, independent of raw probe retention.</summary>
public sealed class HealthStateInterval
{
    private HealthStateInterval() { }

    public HealthStateInterval(Guid targetId, byte state, byte reason, DateTimeOffset startedAt)
    {
        if (targetId.Version != 7 || state > 4 || reason > 9) throw new ArgumentException("Invalid health interval.");
        TargetId = targetId;
        State = state;
        Reason = reason;
        StartedAt = startedAt.ToUniversalTime();
    }

    public long RowId { get; private set; }
    public Guid TargetId { get; private set; }
    public byte State { get; private set; }
    public byte Reason { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public void Close(DateTimeOffset at)
    {
        at = at.ToUniversalTime();
        if (EndedAt is not null || at < StartedAt) throw new InvalidOperationException("Health intervals must be ordered and closed once.");
        EndedAt = at;
    }
}
