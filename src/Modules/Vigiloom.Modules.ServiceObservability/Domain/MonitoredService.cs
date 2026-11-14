using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>A centrally approved application service, independent of host devices.</summary>
public sealed class MonitoredService
{
    // A publication transaction has committed only after its caller returns from CommitAsync.
    // Until a post-commit fence is persisted, public history must remain unavailable.
    public static readonly DateTimeOffset PendingPublicSince = DateTimeOffset.MaxValue;
    private MonitoredService() { }

    public MonitoredService(ServiceCandidate candidate, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Id = Guid.CreateVersion7();
        CandidateId = candidate.Id;
        SourceId = candidate.SourceId;
        ServiceKey = candidate.ServiceKey;
        ServiceNamespace = candidate.ServiceNamespace;
        Environment = candidate.Environment;
        DisplayName = candidate.DisplayName;
        ApprovedAt = CreatedAt = UpdatedAt = approvedAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid? CandidateId { get; private set; }
    public string ServiceKey { get; private set; } = "";
    public string ServiceNamespace { get; private set; } = "";
    public byte[] ServiceNamespaceBytes { get; private set; } = [];
    public string Environment { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public DateTimeOffset ApprovedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public bool IsPublic { get; private set; }
    public DateTimeOffset? PublicSince { get; private set; }
    public string? PublicDisplayName { get; private set; }
    public string? PublicPolicyVersion { get; private set; }
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

    public void SetPublication(bool isPublic, string? displayName, string? policyVersion, DateTimeOffset now)
    {
        if (isPublic && (policyVersion is not (PublicationPolicy.BaselineVersion or PublicationPolicy.ServiceHistoryVersion) || !PublicationPolicy.TryValidateName(displayName, out _)))
            throw new ArgumentException("An approved publication policy and public name are required.");
        if (isPublic && (!IsPublic || policyVersion != PublicPolicyVersion)) PublicSince = PendingPublicSince;
        if (!isPublic) PublicSince = null;
        IsPublic = isPublic;
        if (isPublic) { PublicDisplayName = displayName; PublicPolicyVersion = policyVersion; }
        UpdatedAt = now.ToUniversalTime();
    }

    public void ConfirmPublicSince(DateTimeOffset committedAfter)
    {
        if (!IsPublic || PublicSince != PendingPublicSince || committedAfter.Offset != TimeSpan.Zero || committedAfter >= PendingPublicSince)
            throw new InvalidOperationException("The publication period is not pending.");
        PublicSince = committedAfter;
    }
}
