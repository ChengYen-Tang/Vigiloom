using System.Text.Json;

namespace Vigiloom.Contracts.Agent;

/// <summary>Version 1 accepts only Agent runtime observations, in the original message order.</summary>
public sealed record AgentBatch(int SchemaVersion, Guid BatchId, DateTimeOffset EnqueuedAt, IReadOnlyList<AgentRuntimeMessage> Items);

/// <summary>An observation of the actual Agent runtime, not an assertion that individual collectors are supported.</summary>
public sealed record AgentRuntimeMessage(Guid MessageId, string Kind, DateTimeOffset OccurredAt,
    string OperatingSystem, string Architecture, string RuntimeVersion);

/// <summary>Bounds and validates the uncompressed, immutable wire representation.</summary>
public static class AgentBatchCodec
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumItems = 1000;
    public const string RuntimeKind = "agent.runtime.v1";
    private static readonly JsonSerializerOptions Options = AgentBatchJson.CreateOptions<AgentRuntimeMessage>();

    public static byte[] Serialize(AgentBatch batch)
    {
        if (!IsValid(batch)) throw new ArgumentException("Invalid Agent batch.", nameof(batch));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, Options);
        return bytes.Length <= MaximumBytes ? bytes : throw new ArgumentException("Agent batch exceeds its uncompressed size limit.", nameof(batch));
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out AgentBatch? batch)
    {
        batch = null;
        if (bytes.Length is < 1 or > MaximumBytes) return false;
        try
        {
            var decoded = JsonSerializer.Deserialize<AgentBatch>(bytes, Options);
            if (!IsValid(decoded)) return false;
            batch = decoded;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsValid(AgentBatch? batch)
    {
        if (batch is null || batch.SchemaVersion != 1 || batch.BatchId.Version != 7 ||
            batch.EnqueuedAt == default || batch.EnqueuedAt.Offset != TimeSpan.Zero ||
            batch.Items is not { Count: > 0 and <= MaximumItems }) return false;
        var ids = new HashSet<Guid>();
        foreach (var item in batch.Items)
        {
            if (item is null || item.MessageId.Version != 7 || !ids.Add(item.MessageId) || item.Kind != RuntimeKind ||
                item.OccurredAt == default || item.OccurredAt.Offset != TimeSpan.Zero ||
                !ValidText(item.OperatingSystem, 256) || !ValidText(item.Architecture, 32) || !ValidText(item.RuntimeVersion, 128))
                return false;
        }
        return true;
    }

    private static bool ValidText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

}
