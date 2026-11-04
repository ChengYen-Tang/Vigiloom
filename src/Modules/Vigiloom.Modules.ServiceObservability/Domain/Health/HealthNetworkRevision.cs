namespace Vigiloom.Modules.ServiceObservability.Domain.Health;

/// <summary>An immutable, administrator-visible network policy revision.</summary>
public sealed class HealthNetworkRevision
{
    private HealthNetworkRevision() { }

    public HealthNetworkRevision(HealthNetworkPolicy policy, Guid actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (actor == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actor));
        PolicyId = policy.Id;
        Generation = policy.Generation;
        NetworksJson = policy.NetworksJson;
        ActorId = actor;
        OccurredAt = at.ToUniversalTime();
    }

    public long RowId { get; private set; }
    public Guid PolicyId { get; private set; }
    public long Generation { get; private set; }
    public string NetworksJson { get; private set; } = "[]";
    public Guid ActorId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
