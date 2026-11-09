namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>A previous central replay allowance kept through its transition deadline.</summary>
public sealed class AgentReplayGeneration
{
    private AgentReplayGeneration() { }

    public AgentReplayGeneration(Guid policyId, int windowSeconds, DateTimeOffset validThrough)
    {
        if (policyId == Guid.Empty) throw new ArgumentException("A replay policy is required.", nameof(policyId));
        if (windowSeconds is < 1 or > 2592000) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        PolicyId = policyId;
        WindowSeconds = windowSeconds;
        ValidThrough = validThrough.ToUniversalTime();
    }

    public long RowId { get; private set; }
    public Guid PolicyId { get; private set; }
    public int WindowSeconds { get; private set; }
    public DateTimeOffset ValidThrough { get; private set; }
}
