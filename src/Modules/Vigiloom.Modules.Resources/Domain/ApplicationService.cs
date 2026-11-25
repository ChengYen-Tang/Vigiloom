using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Modules.Resources.Domain;

/// <summary>The approved identity of an application service, independent of a device.</summary>
public sealed class ApplicationService
{
    private ApplicationService() { }

    public ApplicationService(ApplicationCandidate candidate, DateTimeOffset now)
    {
        Id = ResourceIds.Create();
        CandidateId = candidate.Id;
        SourceId = candidate.SourceId;
        ServiceKey = candidate.ServiceKey;
        ServiceNamespace = candidate.ServiceNamespace;
        Environment = candidate.Environment;
        DisplayName = candidate.DisplayName;
        ApprovedAt = CreatedAt = UpdatedAt = now.ToUniversalTime();
    }

    public ApplicationService(Guid sourceId, string serviceKey, string environment, string displayName, string serviceNamespace, DateTimeOffset now)
        : this(new ApplicationCandidate(sourceId, serviceKey, environment, displayName, serviceNamespace, now), now)
    {
        CandidateId = null;
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
    public DateTimeOffset? ArchivedAt { get; private set; }
    public bool IsPublic { get; private set; }
    public DateTimeOffset? PublicSince { get; private set; }
    public string? PublicDisplayName { get; private set; }
    public string? PublicPolicyVersion { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void Stop(DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = UpdatedAt = now.ToUniversalTime();
    }

    public void Resume(DateTimeOffset now)
    {
        if (ArchivedAt is not null) throw new InvalidOperationException("An archived service cannot resume collection.");
        RevokedAt = null;
        ApprovedAt = UpdatedAt = now.ToUniversalTime();
    }

    public void Archive(DateTimeOffset now)
    {
        Stop(now);
        IsPublic = false;
        PublicSince = null;
        ArchivedAt = UpdatedAt = now.ToUniversalTime();
    }

    public void Restore(DateTimeOffset now)
    {
        ArchivedAt = null;
        UpdatedAt = now.ToUniversalTime();
    }

    public void SetPublication(bool isPublic, string? displayName, string? policyVersion, DateTimeOffset now)
    {
        if (isPublic && ArchivedAt is not null) throw new InvalidOperationException("An archived service cannot be published.");
        if (isPublic && (policyVersion is not (PublicationPolicy.BaselineVersion or PublicationPolicy.ServiceHistoryVersion or PublicationPolicy.ServiceHistoryDetailsVersion) ||
            !PublicationPolicy.TryValidateName(displayName, out _))) throw new ArgumentException("An approved publication policy and public name are required.");
        if (isPublic && (!IsPublic || policyVersion != PublicPolicyVersion)) PublicSince = DateTimeOffset.MaxValue;
        if (!isPublic) PublicSince = null;
        IsPublic = isPublic;
        if (isPublic) { PublicDisplayName = displayName; PublicPolicyVersion = policyVersion; }
        UpdatedAt = now.ToUniversalTime();
    }

    public void ConfirmPublicSince(DateTimeOffset committedAfter)
    {
        if (!IsPublic || PublicSince != DateTimeOffset.MaxValue || committedAfter.Offset != TimeSpan.Zero || committedAfter >= DateTimeOffset.MaxValue)
            throw new InvalidOperationException("The publication period is not pending.");
        PublicSince = committedAfter;
    }
}
