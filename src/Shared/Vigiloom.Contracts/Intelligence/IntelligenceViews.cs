namespace Vigiloom.Contracts.Intelligence;

/// <summary>Non-secret status and policy for an intelligence source.</summary>
public sealed record IntelligenceSourceView(Guid Id, string Key, bool Enabled, int UpdateIntervalSeconds, int StaleAfterSeconds,
    int MaximumBytes, int MaximumRecords, string Version, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastSuccessfulCheckAt,
    string? LastError, bool IsStale, Guid? ActiveRevisionId, string ActivationVersion);

/// <summary>Bounded page of intelligence source status.</summary>
public sealed record IntelligenceSourcePage(IReadOnlyList<IntelligenceSourceView> Items, int Page, bool HasMore);

/// <summary>Non-secret progress and coverage for the GitHub-reviewed advisory source.</summary>
public sealed record AdvisoryImportProgressView(Guid SourceId, Guid? ImportId, string State, DateOnly? WindowStart, DateOnly? WindowEnd,
    DateTimeOffset? StartedAt, int PageCount, int RecordCount, long TotalBytes, DateTimeOffset? RetryAt,
    string? FailureCode, Guid? CandidateRevisionId, string Coverage);

/// <summary>Source and validation metadata for a retained intelligence revision.</summary>
public sealed record IntelligenceRevisionView(Guid Id, string UpstreamVersion, DateTimeOffset? ReleasedAt, DateTimeOffset RetrievedAt,
    string Sha256, string SchemaVersion, string MappingVersion, string LicenseEvidence, int RecordCount, bool Revoked, bool Normalized, bool Active);

/// <summary>Bounded page of retained source revisions.</summary>
public sealed record IntelligenceRevisionPage(IReadOnlyList<IntelligenceRevisionView> Items, int Page, bool HasMore);

/// <summary>Expected selection and explicit confirmation for a revision activation.</summary>
public sealed record IntelligenceActivationRequest(Guid RevisionId, Guid? ExpectedActiveRevisionId, string ExpectedVersion, bool ConfirmLargeReduction);

/// <summary>Administrator-controlled source policy and optimistic concurrency version.</summary>
public sealed record IntelligencePolicyUpdate(bool Enabled, int UpdateIntervalSeconds, int StaleAfterSeconds,
    int MaximumBytes, int MaximumRecords, string Version);

public static class IntelligencePolicyLimits
{
    public static bool Valid(int interval, int stale, int bytes, int records) => interval is >= 300 and <= 604800 &&
        stale is >= 3600 and <= 2592000 && bytes is >= 1024 and <= 67108864 && records is >= 1 and <= 1000000;
}
