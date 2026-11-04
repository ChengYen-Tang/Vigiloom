namespace Vigiloom.Contracts.Services;

/// <summary>Last persisted data per source; null means no retained record, not an offline diagnosis.</summary>
public sealed record SourceReceptionSummary(Guid SourceId, string Name, DateTimeOffset? RevokedAt,
    DateTimeOffset? LastLogAt, DateTimeOffset? LastMetricAt, DateTimeOffset? LastTraceAt);
