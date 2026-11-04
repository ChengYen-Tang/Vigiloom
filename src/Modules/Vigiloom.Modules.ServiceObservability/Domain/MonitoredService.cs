namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>A centrally approved application service, independent of host devices.</summary>
public sealed class MonitoredService
{
    private MonitoredService() { }

    public MonitoredService(ServiceCandidate candidate, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Id = Guid.CreateVersion7();
        CandidateId = candidate.Id;
        SourceId = candidate.SourceId;
        ServiceKey = candidate.ServiceKey;
        Environment = candidate.Environment;
        DisplayName = candidate.DisplayName;
        ApprovedAt = CreatedAt = UpdatedAt = approvedAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid? CandidateId { get; private set; }
    public string ServiceKey { get; private set; } = "";
    public string Environment { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public DateTimeOffset ApprovedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public bool IsPublic { get; private set; }
    public byte[] Version { get; private set; } = [];
    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = UpdatedAt = now.ToUniversalTime();
    }

    public void Resume(DateTimeOffset now)
    {
        if (RevokedAt is null) return;
        RevokedAt = null;
        ApprovedAt = UpdatedAt = now.ToUniversalTime();
    }
}
