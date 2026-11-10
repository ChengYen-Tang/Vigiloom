namespace Vigiloom.Contracts.Agent;

/// <summary>A host log or recorded collection gap. A missing source time remains unknown.</summary>
public sealed record HostLogEntry(Guid AgentId, Guid MessageId, Guid SourceId, AgentLogSourceKind SourceKind,
    string SourceName, int ParserVersion, DateTimeOffset? OccurredAt, DateTimeOffset ReceivedAt,
    string Content, string? Provider, long? EventId, string? Level, string? User, string? Process,
    AgentLogGap? Gap);

/// <summary>A bounded receipt-time page; the continuation token is opaque to clients.</summary>
public sealed record HostLogPage(IReadOnlyList<HostLogEntry> Items, string? NextCursor);
