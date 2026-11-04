namespace Vigiloom.Modules.ServiceObservability.Domain.Health;

/// <summary>Durable scheduling state for one health target, separate from editable configuration.</summary>
public sealed class HealthSchedule
{
    private HealthSchedule() { }

    public HealthSchedule(Guid targetId, bool enabled, DateTimeOffset dueAt)
    {
        if (targetId.Version != 7) throw new ArgumentException("A valid health target is required.", nameof(targetId));
        TargetId = targetId;
        Enabled = enabled;
        DueAt = dueAt.ToUniversalTime();
    }

    public long RowId { get; private set; }
    public Guid TargetId { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public Guid? LeaseOwner { get; private set; }
    public long Generation { get; private set; }
    public byte[] Version { get; private set; } = [];
    public byte State { get; private set; }
    public DateTimeOffset? EvidenceAt { get; private set; }
    public byte Streak { get; private set; }
    public int StreakCount { get; private set; }
    public DateTimeOffset? StreakAt { get; private set; }
    public DateTimeOffset? LastEvaluatedAt { get; private set; }
    public byte[]? SourceVersion { get; private set; }
    public byte[]? ServiceVersion { get; private set; }
    public byte[]? InstanceVersion { get; private set; }
    public byte[]? TargetVersion { get; private set; }
    public byte[]? NetworkVersion { get; private set; }

    /// <summary>Invalidates any in-flight lease after configuration changes.</summary>
    public void Reset(bool enabled, DateTimeOffset now)
    {
        Generation = checked(Generation + 1);
        LeaseUntil = null;
        LeaseOwner = null;
        Enabled = enabled;
        DueAt = now.ToUniversalTime();
        State = 0; EvidenceAt = null; Streak = 0; StreakCount = 0; StreakAt = null; LastEvaluatedAt = null;
        SourceVersion = null; ServiceVersion = null; InstanceVersion = null; TargetVersion = null; NetworkVersion = null;
    }

    public void SetVersions(byte[] sourceVersion, byte[] serviceVersion, byte[]? instanceVersion, byte[] targetVersion, byte[] networkVersion)
    {
        if (sourceVersion.Length != 8 || serviceVersion.Length != 8 || targetVersion.Length != 8 || networkVersion.Length != 8 ||
            (instanceVersion is not null && instanceVersion.Length != 8)) throw new ArgumentException("Invalid health authorization version.");
        SourceVersion = sourceVersion; ServiceVersion = serviceVersion; InstanceVersion = instanceVersion;
        TargetVersion = targetVersion; NetworkVersion = networkVersion;
    }

    public void SetEvaluation(byte state, DateTimeOffset? evidenceAt, byte streak, int streakCount,
        DateTimeOffset? streakAt, DateTimeOffset? lastEvaluatedAt)
    {
        if (state > 3 || streak > 3 || streakCount is < 0 or > 10) throw new ArgumentException("Invalid evaluation state.");
        State = state; EvidenceAt = evidenceAt; Streak = streak; StreakCount = streakCount;
        StreakAt = streakAt; LastEvaluatedAt = lastEvaluatedAt;
    }

}
