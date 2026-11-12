using System.Text.Json;
using System.Text.Json.Serialization;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Contracts.Agent.Integrity;

public enum IntegrityItemKind { File = 1, Directory = 2, RegistryKey = 3, RegistryValue = 4 }
public enum IntegrityScanOutcome { Complete = 1, Partial = 2, PermissionDenied = 3, Unsupported = 4, Error = 5 }
[Flags]
public enum IntegrityPartialReason { AccessDenied = 1, LimitReached = 2, ChangedDuringScan = 4, SourceUnavailable = 8, Other = 16 }

/// <summary>An observed path and optional SHA-256 digests. Raw contents and registry values are never sent.</summary>
public sealed record IntegrityItem(
    [property: JsonRequired] string Key,
    [property: JsonRequired] IntegrityItemKind Kind,
    string? ContentDigest = null,
    string? SecurityDigest = null,
    string? Owner = null);

/// <summary>An immutable chunk belonging to one authenticated Agent, scope and scan generation.</summary>
public sealed record IntegrityChunk(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] Guid BatchId,
    [property: JsonRequired] DateTimeOffset EnqueuedAt,
    [property: JsonRequired] Guid ScopeId,
    [property: JsonRequired] long ScopeRevision,
    [property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] IntegrityScanOutcome Outcome,
    [property: JsonRequired] bool CoverageGap,
    IntegrityPartialReason? PartialReason,
    [property: JsonRequired] int ChunkIndex,
    [property: JsonRequired] int ChunkCount,
    [property: JsonRequired] int TotalItems,
    [property: JsonRequired] long TotalBytes,
    [property: JsonRequired] byte[] Digest,
    [property: JsonRequired] IReadOnlyList<IntegrityItem> Items);

/// <summary>Validates bounded chunks before they enter the durable queue or central staging.</summary>
public static class IntegrityChunkCodec
{
    public const int SchemaVersion = 6;
    public const int MaximumItemsPerChunk = 100;
    public const int MaximumChunks = 1024;
    public const int MaximumSnapshotItems = 20000;
    public const long MaximumSnapshotBytes = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = AgentBatchJson.CreateOptions<IntegrityItem>(MaximumItemsPerChunk);

    public static byte[] Serialize(IntegrityChunk chunk)
    {
        if (!IsValid(chunk)) throw new ArgumentException("Invalid integrity chunk.", nameof(chunk));
        return BoundedJson.Serialize(chunk, Options);
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out IntegrityChunk? chunk)
    {
        chunk = null;
        if (bytes.Length is < 1 or > AgentBatchCodec.MaximumBytes) return false;
        try
        {
            var value = JsonSerializer.Deserialize<IntegrityChunk>(bytes, Options);
            if (!IsValid(value)) return false;
            chunk = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsValid(IntegrityChunk? chunk)
    {
        if (chunk is null || chunk.SchemaVersion != SchemaVersion || chunk.BatchId.Version != 7 || chunk.ScopeId.Version != 7 ||
            chunk.SnapshotId.Version != 7 || chunk.ScopeRevision < 1 || chunk.Generation < 1 || !Enum.IsDefined(chunk.Outcome) ||
            chunk.EnqueuedAt == default || chunk.EnqueuedAt.Offset != TimeSpan.Zero || chunk.ObservedAt == default || chunk.ObservedAt.Offset != TimeSpan.Zero ||
            chunk.ChunkCount is < 1 or > MaximumChunks || chunk.ChunkIndex < 0 || chunk.ChunkIndex >= chunk.ChunkCount ||
            chunk.TotalItems is < 0 or > MaximumSnapshotItems || chunk.TotalBytes is < 2 or > MaximumSnapshotBytes ||
            chunk.TotalBytes < 2L * chunk.ChunkCount || chunk.TotalItems > 0 && chunk.TotalItems < chunk.ChunkCount ||
            chunk.Digest is not { Length: 32 } || chunk.Items is not { Count: <= MaximumItemsPerChunk } || chunk.Items.Count > chunk.TotalItems)
            return false;
        if (chunk.Outcome == IntegrityScanOutcome.Partial && chunk.PartialReason is null ||
            chunk.Outcome != IntegrityScanOutcome.Partial && chunk.PartialReason is not null ||
            chunk.PartialReason is { } reason && (reason == 0 || (reason & ~(IntegrityPartialReason.AccessDenied | IntegrityPartialReason.LimitReached |
                IntegrityPartialReason.ChangedDuringScan | IntegrityPartialReason.SourceUnavailable | IntegrityPartialReason.Other)) != 0)) return false;
        if (chunk.TotalItems == 0 && (chunk.ChunkCount != 1 || chunk.ChunkIndex != 0 || chunk.Items.Count != 0)) return false;
        if (chunk.TotalItems > 0 && chunk.Items.Count == 0) return false;
        if (chunk.Outcome is IntegrityScanOutcome.PermissionDenied or IntegrityScanOutcome.Unsupported or IntegrityScanOutcome.Error && chunk.TotalItems != 0) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in chunk.Items)
        {
            if (item is null || !IntegrityKey.IsValid(item.Kind, item.Key) || !keys.Add(item.Key) ||
                !ValidDigest(item.ContentDigest) || !ValidDigest(item.SecurityDigest) ||
                item.Owner is { } owner && (string.IsNullOrWhiteSpace(owner) || owner.Length > 256 || owner.Any(char.IsControl)) ||
                item.Kind is IntegrityItemKind.Directory or IntegrityItemKind.RegistryKey && item.ContentDigest is not null ||
                chunk.Outcome == IntegrityScanOutcome.Complete && item.Kind is IntegrityItemKind.File or IntegrityItemKind.RegistryValue && item.ContentDigest is null)
                return false;
        }
        return true;
    }

    private static bool ValidDigest(string? value) => value is null || value.Length == 64 && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
}
