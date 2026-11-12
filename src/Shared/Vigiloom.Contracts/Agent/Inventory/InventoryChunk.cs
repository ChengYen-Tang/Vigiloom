using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>One immutable chunk of a source snapshot. Agent identity comes from authentication.</summary>
public sealed record InventoryChunk(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] Guid BatchId,
    [property: JsonRequired] DateTimeOffset EnqueuedAt,
    [property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] InventorySource Source,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] InventoryOutcome Outcome,
    [property: JsonRequired] int ChunkIndex,
    [property: JsonRequired] int ChunkCount,
    [property: JsonRequired] int TotalItems,
    [property: JsonRequired] long TotalBytes,
    [property: JsonRequired] byte[] Digest,
    [property: JsonRequired] IReadOnlyList<InventoryEntry> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InventoryPartialReason? PartialReason = null);

public enum InventorySource
{
    WindowsSoftware32 = 1, WindowsSoftware64 = 2, LinuxDpkg = 3, LinuxRpm = 4,
    LinuxAccounts = 5, LinuxGroups = 6, WindowsQuickFix = 7, HostSystem = 8,
    WindowsAccounts = 9, WindowsGroups = 10, HostNetwork = 11, HostVolumes = 12,
    HostProcesses = 13, HostServices = 14, HostListeners = 15
}

public enum InventoryOutcome { Complete = 1, Partial = 2, PermissionDenied = 3, Unsupported = 4, Error = 5 }

[Flags]
public enum InventoryPartialReason { Other = 1, AccessDenied = 2, ToolUnavailable = 4, OwnerUnavailable = 8, Truncated = 16 }

/// <summary>Fields are interpreted by source; this contract has no credentials or arbitrary properties.</summary>
public sealed record InventoryEntry(
    [property: JsonRequired] string Key,
    [property: JsonRequired] string Name,
    string? Version = null,
    string? Architecture = null,
    string? Supplier = null,
    uint? NumericId = null,
    uint? PrimaryGroupId = null,
    IReadOnlyList<string>? Members = null,
    string? InstalledOn = null,
    string? OperatingSystem = null,
    string? ProcessArchitecture = null,
    string? Runtime = null,
    int? AvailableProcessorCount = null,
    string? OperatingSystemFamily = null,
    string? DistributionId = null,
    string? DistributionVersion = null,
    DateTimeOffset? BootedAt = null,
    string? AgentVersion = null,
    ulong? TotalMemoryBytes = null,
    string? ResourceType = null,
    string? ParentKey = null,
    string? Address = null,
    long? CapacityBytes = null,
    string? HardwareType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ProcessStartedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ParentProcessId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunAs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutablePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommandLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServiceStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartupMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Protocol = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OwningProcessId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? OwningProcessStartedAt = null);

/// <summary>Bounds the uncompressed snapshot envelope before staging or durable queueing.</summary>
public static class InventoryChunkCodec
{
    public const int SchemaVersion = 4;
    public const int ProcessSchemaVersion = 5;
    public const string MaskedArgumentsSuffix = " [arguments hidden]";
    public const int MaximumItemsPerChunk = 100;
    public const int MaximumChunks = 1024;
    public const int MaximumSnapshotItems = 20_000;
    public const long MaximumSnapshotBytes = 32L * 1024 * 1024;
    public const int MaximumEntryTextLength = 2048;
    public const int MaximumArchitectureLength = 128;
    public const int MaximumMembers = 5000;
    public const int MaximumSnapshotMembers = 100_000;
    public const int MaximumMemberLength = 256;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = AgentBatchJson.CreateOptions<InventoryEntry>(MaximumItemsPerChunk);
        options.Converters.Add(new BoundedMembersConverter());
        return options;
    }

    public static byte[] Serialize(InventoryChunk chunk)
    {
        if (!IsValid(chunk)) throw new ArgumentException("Invalid inventory chunk.", nameof(chunk));
        return BoundedJson.Serialize(chunk, Options);
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out InventoryChunk? chunk)
    {
        chunk = null;
        if (bytes.Length is < 1 or > AgentBatchCodec.MaximumBytes) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<InventoryChunk>(bytes, Options);
            if (!IsValid(parsed)) return false;
            chunk = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsValid(InventoryChunk? chunk)
    {
        if (chunk is null || !VersionMatchesSource(chunk.SchemaVersion, chunk.Source) || chunk.BatchId.Version != 7 || chunk.SnapshotId.Version != 7 ||
            chunk.Generation < 1 || !Enum.IsDefined(chunk.Source) || !Enum.IsDefined(chunk.Outcome) ||
            chunk.EnqueuedAt == default || chunk.EnqueuedAt.Offset != TimeSpan.Zero ||
            chunk.ObservedAt == default || chunk.ObservedAt.Offset != TimeSpan.Zero ||
            chunk.ChunkCount is < 1 or > MaximumChunks || chunk.ChunkIndex < 0 || chunk.ChunkIndex >= chunk.ChunkCount ||
            chunk.TotalItems is < 0 or > MaximumSnapshotItems || chunk.TotalBytes is < 0 or > MaximumSnapshotBytes ||
            chunk.TotalBytes < 2L * chunk.ChunkCount || (chunk.TotalItems > 0 && chunk.TotalItems < chunk.ChunkCount) ||
            chunk.Digest is not { Length: 32 } || chunk.Items is not { Count: <= MaximumItemsPerChunk } ||
            chunk.Items.Count > chunk.TotalItems) return false;
        if (chunk.PartialReason is { } reason && (chunk.SchemaVersion != ProcessSchemaVersion || chunk.Outcome != InventoryOutcome.Partial ||
            reason == 0 || (reason & ~(InventoryPartialReason.Other | InventoryPartialReason.AccessDenied | InventoryPartialReason.ToolUnavailable | InventoryPartialReason.OwnerUnavailable | InventoryPartialReason.Truncated)) != 0)) return false;
        if (chunk.TotalItems == 0 && (chunk.ChunkCount != 1 || chunk.ChunkIndex != 0 || chunk.Items.Count != 0)) return false;
        if (chunk.TotalItems > 0 && chunk.Items.Count == 0) return false;
        if (chunk.Outcome is InventoryOutcome.PermissionDenied or InventoryOutcome.Unsupported or InventoryOutcome.Error && chunk.TotalItems != 0) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var memberCount = 0;
        foreach (var item in chunk.Items)
        {
            if (item is null || !ValidText(item.Key, 2048, false) || !keys.Add(item.Key) || !ValidText(item.Name, 2048, false) ||
                !ValidText(item.Version, 2048, true) || !ValidText(item.Architecture, 128, true) ||
                !ValidText(item.Supplier, 2048, true) || !ValidText(item.InstalledOn, 2048, true) ||
                !ValidText(item.OperatingSystem, 2048, true) || !ValidText(item.ProcessArchitecture, 128, true) ||
                !ValidText(item.Runtime, 2048, true) || item.AvailableProcessorCount is <= 0 ||
                !ValidText(item.OperatingSystemFamily, 128, true) || !ValidText(item.DistributionId, 256, true) ||
                !ValidText(item.DistributionVersion, 256, true) || !ValidText(item.AgentVersion, 256, true) ||
                item.TotalMemoryBytes is 0 ||
                !ValidText(item.ResourceType, 128, true) || !ValidText(item.ParentKey, 2048, true) ||
                !ValidText(item.Address, 256, true) || item.CapacityBytes is < 0 ||
                !ValidText(item.HardwareType, 128, true) ||
                !ValidText(item.RunAs, 256, true) || !ValidText(item.ExecutablePath, 2048, true) ||
                !ValidText(item.CommandLine, 2048, true) || !ValidText(item.ServiceStatus, 128, true) ||
                !ValidText(item.StartupMode, 128, true) || !ValidText(item.Protocol, 16, true) ||
                item.ParentProcessId is < 0 || item.OwningProcessId is < 0 || item.Port is < 0 or > 65535 ||
                item.ProcessStartedAt is { } processStart && (processStart == default || processStart.Offset != TimeSpan.Zero) ||
                item.OwningProcessStartedAt is { } ownerStart && (ownerStart == default || ownerStart.Offset != TimeSpan.Zero) ||
                chunk.SchemaVersion == SchemaVersion && HasProcessFields(item) ||
                chunk.Source != InventorySource.HostProcesses && (item.CommandLine is not null || item.ExecutablePath is not null) ||
                item.BootedAt is { } boot && (boot == default || boot.Offset != TimeSpan.Zero) ||
                item.Members is { Count: > MaximumMembers } || item.Members?.Any(member => !ValidText(member, MaximumMemberLength, false)) == true ||
                !ValidForSource(chunk.Source, item)) return false;
            memberCount += item.Members?.Count ?? 0;
            if (memberCount > MaximumSnapshotMembers) return false;
        }
        return true;
    }

    private static bool ValidText(string? value, int limit, bool optional) =>
        value is null ? optional : value.Length <= limit && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool VersionMatchesSource(int version, InventorySource source) => source switch
    {
        >= InventorySource.HostProcesses and <= InventorySource.HostListeners => version == ProcessSchemaVersion,
        _ => version == SchemaVersion
    };

    private static bool HasProcessFields(InventoryEntry item) => item.ProcessStartedAt is not null || item.ParentProcessId is not null ||
        item.RunAs is not null || item.ExecutablePath is not null || item.CommandLine is not null || item.ServiceStatus is not null ||
        item.StartupMode is not null || item.Protocol is not null || item.Port is not null || item.OwningProcessId is not null ||
        item.OwningProcessStartedAt is not null;

    private static bool ValidForSource(InventorySource source, InventoryEntry item) => source switch
    {
        InventorySource.LinuxDpkg or InventorySource.LinuxRpm => item.Version is not null && item.Architecture is not null,
        InventorySource.LinuxAccounts => item.NumericId is not null && item.PrimaryGroupId is not null && item.Members is null,
        InventorySource.LinuxGroups => item.NumericId is not null && item.PrimaryGroupId is null && item.Members is not null,
        InventorySource.HostSystem => item.OperatingSystem is not null && item.Version is not null && item.Architecture is not null &&
            item.ProcessArchitecture is not null && item.Runtime is not null && item.AvailableProcessorCount is not null,
        InventorySource.HostNetwork => item.ResourceType switch
        {
            "interface" => item.ParentKey is null && item.Address is null,
            "address" => item.ParentKey is not null && item.Address is not null,
            _ => false
        },
        InventorySource.HostVolumes => item.ResourceType == "volume",
        InventorySource.HostProcesses => item.NumericId is not null && item.ProcessStartedAt is not null &&
            (item.CommandLine is null || item.ExecutablePath is not null && item.CommandLine == item.ExecutablePath + MaskedArgumentsSuffix),
        InventorySource.HostServices => item.ServiceStatus is not null,
        InventorySource.HostListeners => item.Address is not null && item.Port is > 0 && item.Protocol is "tcp" or "udp",
        _ => true
    };

    private sealed class BoundedMembersConverter : JsonConverter<IReadOnlyList<string>>
    {
        public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Members must be an array.");
            var members = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return members;
                if (members.Count >= MaximumMembers || reader.TokenType != JsonTokenType.String) throw new JsonException("Invalid inventory members.");
                var member = reader.GetString();
                if (!ValidText(member, MaximumMemberLength, false)) throw new JsonException("Invalid inventory member.");
                members.Add(member!);
            }
            throw new JsonException("Incomplete inventory members.");
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var member in value) writer.WriteStringValue(member);
            writer.WriteEndArray();
        }
    }
}
