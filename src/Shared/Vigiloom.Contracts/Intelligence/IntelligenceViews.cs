namespace Vigiloom.Contracts.Intelligence;

/// <summary>Non-secret status and policy for an intelligence source.</summary>
public sealed record IntelligenceSourceView(Guid Id, string Key, bool Enabled, int UpdateIntervalSeconds, int StaleAfterSeconds,
    int MaximumBytes, int MaximumRecords, string Version, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastSuccessfulCheckAt,
    string? LastError, bool IsStale, Guid? ActiveRevisionId, string ActivationVersion, bool HasCredential,
    IntelligenceSourceDescription? Description, bool ActiveUnavailable = false, bool ActiveVersionOutdated = false);

/// <summary>Verified source identity and scope; a missing description means the source is unrecognized.</summary>
public sealed record IntelligenceSourceDescription(string Name, string Category, string SourceUrl, string Coverage, string LicenseEvidence, string PlatformCoverage);

/// <summary>Bounded page of intelligence source status.</summary>
public sealed record IntelligenceSourcePage(IReadOnlyList<IntelligenceSourceView> Items, int Page, bool HasMore);

/// <summary>Non-secret progress and coverage for the GitHub-reviewed advisory source.</summary>
public sealed record AdvisoryImportProgressView(Guid SourceId, Guid? ImportId, string State, DateOnly? WindowStart, DateOnly? WindowEnd,
    DateTimeOffset? StartedAt, int PageCount, int RecordCount, long TotalBytes, DateTimeOffset? RetryAt,
    string? FailureCode, Guid? CandidateRevisionId, string Coverage, string? Version);

/// <summary>Identifies the failed import and version the administrator reviewed.</summary>
public sealed record AdvisoryImportRecoveryRequest(Guid ImportId, string Version);

/// <summary>Replaces or removes the centrally protected GitHub token without returning its value.</summary>
public sealed record AdvisoryCredentialUpdate(string Version, bool Clear, string? Credential);

/// <summary>Source and validation metadata for a retained intelligence revision.</summary>
public sealed record IntelligenceRevisionView(Guid Id, string UpstreamVersion, DateTimeOffset? ReleasedAt, DateTimeOffset RetrievedAt,
    string Sha256, string SchemaVersion, string MappingVersion, string LicenseEvidence, int RecordCount, bool Revoked, bool Normalized, bool Active,
    DateTimeOffset? LastOfflineImportAt = null);

/// <summary>Bounded page of retained source revisions.</summary>
public sealed record IntelligenceRevisionPage(IReadOnlyList<IntelligenceRevisionView> Items, int Page, bool HasMore);

/// <summary>Retained Sigma source metadata; a parsed rule is not an enabled detection.</summary>
public sealed record SigmaRuleView(string Path, string? RuleId, string Title, string? Author, string? Status, string? License,
    string? Product, string? Category, string? Service, string Sha256, string LicenseStatus, string? ExclusionReason);
public sealed record SigmaRulePage(IReadOnlyList<SigmaRuleView> Items, int Page, bool HasMore);

/// <summary>Metadata of retained IOC, YARA or license evidence, without distributing its source bytes.</summary>
public sealed record SignatureFileView(string Path, string Kind, string Sha256, int? Entries, int? UnsupportedEntries);
/// <summary>A bounded page of retained Signature-Base file metadata.</summary>
public sealed record SignatureFilePage(IReadOnlyList<SignatureFileView> Items, int Page, bool HasMore);

/// <summary>Location and reason for an IOC line that cannot be interpreted.</summary>
public sealed record IocIssueView(int LineNumber, string Reason);
/// <summary>A bounded page of unsupported or invalid IOC lines.</summary>
public sealed record IocIssuePage(IReadOnlyList<IocIssueView> Items, int Page, bool HasMore);

/// <summary>A source line that may mention an author or license; it is not a parsed rule or authorization decision.</summary>
public sealed record SignatureEvidenceLine(int Number, string Text, bool Truncated);
/// <summary>Bounded candidate lines from one retained source file.</summary>
public sealed record SignatureEvidenceView(string Path, string Sha256, IReadOnlyList<SignatureEvidenceLine> Lines, bool Truncated,
    bool InvalidEncoding, bool IntegrityValid, int? ContextLine = null, IReadOnlyList<SignatureEvidenceLine>? Context = null, int Page = 1, bool ContextHasMore = false);

/// <summary>Expected selection and explicit confirmation for a revision activation.</summary>
public sealed record IntelligenceActivationRequest(Guid RevisionId, Guid? ExpectedActiveRevisionId, string ExpectedVersion, bool ConfirmLargeReduction, bool ConfirmOfflineSource = false);

/// <summary>Administrator-controlled source policy and optimistic concurrency version.</summary>
public sealed record IntelligencePolicyUpdate(bool Enabled, int UpdateIntervalSeconds, int StaleAfterSeconds,
    int MaximumBytes, int MaximumRecords, string Version);

public static class IntelligencePolicyLimits
{
    public static bool Valid(int interval, int stale, int bytes, int records) => interval is >= 300 and <= 604800 &&
        stale is >= 3600 and <= 2592000 && bytes is >= 1024 and <= 1073741824 && records is >= 1 and <= 1000000;

    public static bool ValidForSource(string key, int bytes, int records) => key switch
    {
        "github-reviewed" or "msrc-cvrf" => bytes <= 1073741824 && records <= 100000,
        "debian-security" or "ubuntu-osv" => bytes <= 268435456 && records <= 1000000,
        "sigmahq-rules" or "signature-base" => bytes <= 67108864 && records <= 50000,
        _ => bytes <= 67108864
    };
}
