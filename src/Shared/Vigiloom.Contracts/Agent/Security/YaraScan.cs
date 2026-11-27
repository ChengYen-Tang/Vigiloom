using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent.Security;

/// <summary>An administrator-requested scan of one file on one Agent.</summary>
public sealed record YaraScanRequest(string FilePath, string Rules, long MaximumBytes, int TimeoutSeconds);

public sealed record YaraScanJob(Guid Id, Guid AgentId, Guid DeviceId, Guid RequestedBy, YaraScanRequest Request,
    DateTimeOffset RequestedAt, YaraScanReport? Result, DateTimeOffset? StartedAt = null);

/// <summary>A scan start reported by an Agent through the host service.</summary>
public sealed record YaraScanStarted(Guid ScanId, Guid AgentId, Guid DeviceId, DateTimeOffset StartedAt);

public enum YaraScanOutcome { Completed = 1, FileTooLarge = 2, TimedOut = 3, Unavailable = 4, Failed = 5, Cancelled = 6 }

public sealed record YaraScanReport(YaraScanOutcome Outcome, string Output, string Error, bool OutputTruncated, DateTimeOffset CompletedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngineVersion = null);

public sealed record YaraResultBatch(int SchemaVersion, Guid BatchId, DateTimeOffset EnqueuedAt,
    [property: JsonRequired] YaraScanReport Result);

public static class YaraResultBatchCodec
{
    public const int SchemaVersion = 12;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(YaraResultBatch batch)
    {
        if (!Valid(batch)) throw new ArgumentException("Invalid YARA result.", nameof(batch));
        var payload = JsonSerializer.SerializeToUtf8Bytes(batch, Options);
        return payload.Length <= AgentBatchCodec.MaximumBytes ? payload : throw new ArgumentException("YARA result is too large.", nameof(batch));
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out YaraResultBatch? batch)
    {
        batch = null;
        if (bytes.Length is < 1 or > AgentBatchCodec.MaximumBytes) return false;
        try
        {
            var value = JsonSerializer.Deserialize<YaraResultBatch>(bytes, Options);
            if (!Valid(value)) return false;
            batch = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Valid(YaraResultBatch? batch) => batch is { SchemaVersion: SchemaVersion, Result: not null } &&
        batch.BatchId.Version == 7 && batch.EnqueuedAt != default && batch.EnqueuedAt.Offset == TimeSpan.Zero &&
        Enum.IsDefined(batch.Result.Outcome) && batch.Result.Output is { Length: <= 65536 } && batch.Result.Error is { Length: <= 65536 } &&
        batch.Result.CompletedAt != default && batch.Result.CompletedAt.Offset == TimeSpan.Zero && batch.Result.EngineVersion is null or { Length: <= 64 };
}
