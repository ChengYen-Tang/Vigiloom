using System.Text.Json;

namespace Vigiloom.Contracts.Agent;

/// <summary>A bounded, immutable batch from one configured host log source.</summary>
public sealed record AgentLogBatch(int SchemaVersion, Guid BatchId, DateTimeOffset EnqueuedAt,
    Guid SourceId, AgentLogSourceKind SourceKind, string SourceName, int ParserVersion,
    IReadOnlyList<AgentLogRecord> Items);

/// <summary>Original content and optional fields observed at the source.</summary>
public sealed record AgentLogRecord(Guid MessageId, DateTimeOffset? OccurredAt, string Content,
    string? Provider, long? EventId, string? Level, string? User, string? Process, AgentLogGap? Gap = null);

/// <summary>A known collection interruption. A null count means the number of missing records is unknown.</summary>
public sealed record AgentLogGap(AgentLogGapReason Reason, long? AffectedCount);
public enum AgentLogGapReason { CursorLost = 1, Oversized = 2, InvalidEncoding = 3, SourceUnavailable = 4, ConfigurationReset = 5 }

public enum AgentLogSourceKind { WindowsEvent = 1, Journal = 2, TextFile = 3 }

/// <summary>Validates the versioned wire format before queueing or receiving host logs.</summary>
public static class AgentLogBatchCodec
{
    public const int SchemaVersion = 3;
    public const int MaximumItems = 100;
    public const int MaximumContentCharacters = 65_536;
    private static readonly JsonSerializerOptions Options = AgentBatchJson.CreateOptions<AgentLogRecord>(MaximumItems);

    public static byte[] Serialize(AgentLogBatch batch)
    {
        if (!IsValid(batch)) throw new ArgumentException("Invalid Agent log batch.", nameof(batch));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, Options);
        return bytes.Length <= AgentBatchCodec.MaximumBytes ? bytes : throw new ArgumentException("Agent log batch is too large.", nameof(batch));
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out AgentLogBatch? batch)
    {
        batch = null;
        if (bytes.Length is < 1 or > AgentBatchCodec.MaximumBytes) return false;
        try
        {
            var decoded = JsonSerializer.Deserialize<AgentLogBatch>(bytes, Options);
            if (!IsValid(decoded)) return false;
            batch = decoded;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsValid(AgentLogBatch? batch)
    {
        if (batch is null || batch.SchemaVersion != SchemaVersion || batch.BatchId.Version != 7 ||
            batch.SourceId.Version != 7 || batch.EnqueuedAt == default || batch.EnqueuedAt.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(batch.SourceKind) || !ValidName(batch.SourceName, 512) || batch.ParserVersion < 1 ||
            batch.Items is not { Count: > 0 and <= MaximumItems }) return false;
        var ids = new HashSet<Guid>();
        foreach (var item in batch.Items)
        {
            if (item is null || item.MessageId.Version != 7 || !ids.Add(item.MessageId) ||
                (item.OccurredAt is { } occurredAt && (occurredAt == default || occurredAt.Offset != TimeSpan.Zero)) ||
                item.Content is null || item.Content.Length > MaximumContentCharacters ||
                !ValidOptional(item.Provider, 256) || item.EventId < 0 || !ValidOptional(item.Level, 64) ||
                !ValidOptional(item.User, 256) || !ValidOptional(item.Process, 256) ||
                (item.Gap is { } gap && (!Enum.IsDefined(gap.Reason) || gap.AffectedCount is <= 0 || item.Content.Length != 0 ||
                    item.Provider is not null || item.EventId is not null || item.Level is not null || item.User is not null || item.Process is not null))) return false;
        }
        return true;
    }

    private static bool ValidName(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static bool ValidOptional(string? value, int limit) => value is null || ValidName(value, limit);
}
