namespace Vigiloom.Contracts.Intelligence;

/// <summary>An indicator observed in a host event. A match is a lead, not a compromise verdict.</summary>
public sealed record IocDetectionView(Guid AgentId, Guid MessageId, Guid SourceId, Guid RevisionId,
    string Kind, string Value, string Field, int SourceLine, string SourceUrl, string LicenseUrl,
    DateTimeOffset? OccurredAt, DateTimeOffset ReceivedAt, DateTimeOffset EvaluatedAt);

public sealed record IocDetectionPage(IReadOnlyList<IocDetectionView> Items, int Page, bool HasMore);
