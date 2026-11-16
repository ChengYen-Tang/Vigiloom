using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent.Baseline;

public enum BaselineOutcome { Pass = 1, Fail = 2, NotApplicable = 3, Unknown = 4, PermissionDenied = 5, Error = 6 }

/// <summary>One read-only assessment against a published source revision.</summary>
public sealed record BaselineResult(
    Guid SourceId, Guid RevisionId, string ActivationVersion, string CheckId,
    DateTimeOffset ObservedAt, string OperatingSystem, BaselineOutcome Outcome, string? Evidence);

/// <summary>A durable, bounded batch; Agent identity is provided by authentication.</summary>
public sealed record BaselineBatch(int SchemaVersion, Guid BatchId, DateTimeOffset EnqueuedAt,
    [property: JsonRequired] IReadOnlyList<BaselineResult> Items);

public static class BaselineBatchCodec
{
    public const int SchemaVersion = 11;
    public const int MaximumResults = 100;
    private static readonly JsonSerializerOptions Options = AgentBatchJson.CreateOptions<BaselineResult>(MaximumResults);

    public static byte[] Serialize(BaselineBatch batch)
    {
        if (!IsValid(batch)) throw new ArgumentException("Invalid baseline batch.", nameof(batch));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, Options);
        return bytes.Length <= AgentBatchCodec.MaximumBytes ? bytes : throw new ArgumentException("Baseline batch is too large.", nameof(batch));
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out BaselineBatch? batch)
    {
        batch = null;
        if (bytes.Length is < 1 or > AgentBatchCodec.MaximumBytes) return false;
        try
        {
            var value = JsonSerializer.Deserialize<BaselineBatch>(bytes, Options);
            if (!IsValid(value)) return false;
            batch = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsValid(BaselineBatch? batch) =>
        batch is { SchemaVersion: SchemaVersion, Items.Count: > 0 and <= MaximumResults } &&
        batch.BatchId.Version == 7 && batch.EnqueuedAt != default && batch.EnqueuedAt.Offset == TimeSpan.Zero &&
        batch.Items.All(item => item is not null && item.SourceId.Version == 7 && item.RevisionId.Version == 7 &&
            BaselineSelection.IsActivationVersionValid(item.ActivationVersion) && Text(item.CheckId, 128) && Text(item.OperatingSystem, 128) &&
            item.ObservedAt != default && item.ObservedAt.Offset == TimeSpan.Zero && Enum.IsDefined(item.Outcome) &&
            (item.Evidence is null || item.Evidence.Length <= 512 && !item.Evidence.Any(char.IsControl)));

    private static bool Text(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
}
