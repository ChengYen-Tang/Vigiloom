using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>A host log record committed for an authenticated Agent.</summary>
public sealed class HostLog
{
    private HostLog() { }

    public HostLog(Guid agentId, AgentLogBatch batch, int ordinal, DateTimeOffset receivedAt)
    {
        var item = batch.Items[ordinal];
        AgentId = agentId; BatchId = batch.BatchId; Ordinal = ordinal; MessageId = item.MessageId;
        SourceId = batch.SourceId; SourceKind = batch.SourceKind; SourceName = batch.SourceName;
        ParserVersion = batch.ParserVersion; OccurredAt = item.OccurredAt; ReceivedAt = receivedAt.ToUniversalTime();
        Content = item.Content; Provider = item.Provider; EventId = item.EventId; Level = item.Level;
        User = item.User; Process = item.Process; GapReason = item.Gap?.Reason; GapAffectedCount = item.Gap?.AffectedCount;
    }

    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid BatchId { get; private set; }
    public int Ordinal { get; private set; }
    public Guid MessageId { get; private set; }
    public Guid SourceId { get; private set; }
    public AgentLogSourceKind SourceKind { get; private set; }
    public string SourceName { get; private set; } = "";
    public int ParserVersion { get; private set; }
    public DateTimeOffset? OccurredAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public string Content { get; private set; } = "";
    public string? Provider { get; private set; }
    public long? EventId { get; private set; }
    public string? Level { get; private set; }
    public string? User { get; private set; }
    public string? Process { get; private set; }
    public AgentLogGapReason? GapReason { get; private set; }
    public long? GapAffectedCount { get; private set; }

    public bool Matches(AgentLogBatch batch, AgentLogRecord item) => SourceId == batch.SourceId &&
        SourceKind == batch.SourceKind && string.Equals(SourceName, batch.SourceName, StringComparison.Ordinal) &&
        ParserVersion == batch.ParserVersion && OccurredAt == item.OccurredAt &&
        string.Equals(Content, item.Content, StringComparison.Ordinal) && string.Equals(Provider, item.Provider, StringComparison.Ordinal) &&
        EventId == item.EventId && string.Equals(Level, item.Level, StringComparison.Ordinal) &&
        string.Equals(User, item.User, StringComparison.Ordinal) && string.Equals(Process, item.Process, StringComparison.Ordinal) &&
        GapReason == item.Gap?.Reason && GapAffectedCount == item.Gap?.AffectedCount;
}
