using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>Logical limits for retained Agent batch payloads; filesystem overhead is additional.</summary>
public sealed record AgentQueueCapacity([property: JsonRequired] long PayloadBytes, [property: JsonRequired] int BatchCount)
{
    public const long MinimumBytes = 1024L * 1024;
    public const long MaximumBytes = 1024L * 1024 * 1024;
    public const int MaximumBatches = 100_000;
    public static AgentQueueCapacity Default { get; } = new(256L * 1024 * 1024, MaximumBatches);
    public bool IsValid() => PayloadBytes is >= MinimumBytes and <= MaximumBytes && PayloadBytes % MinimumBytes == 0 &&
        BatchCount is >= 1 and <= MaximumBatches;
}

/// <summary>Independent capacity-group revision, separate from transport scheduling settings.</summary>
public sealed record AgentQueueCapacitySnapshot([property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Revision, [property: JsonRequired] AgentQueueCapacity Capacity)
{
    public const int CurrentSchema = 1;
    public static AgentQueueCapacitySnapshot Create(AgentQueueCapacity capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        if (!capacity.IsValid()) throw new ArgumentException("Invalid Agent queue capacity.", nameof(capacity));
        return new(CurrentSchema, ComputeRevision(capacity), capacity);
    }
    public bool IsValid() => SchemaVersion == CurrentSchema && Capacity is not null && Capacity.IsValid() &&
        string.Equals(Revision, ComputeRevision(Capacity), StringComparison.Ordinal);
    private static string ComputeRevision(AgentQueueCapacity capacity)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32BigEndian(bytes, CurrentSchema);
        BinaryPrimitives.WriteInt64BigEndian(bytes[4..], capacity.PayloadBytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], capacity.BatchCount);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

/// <summary>Null values inherit the corresponding platform or built-in limit.</summary>
public sealed record AgentQueueCapacityOverrides([property: JsonRequired] long? PayloadBytes, [property: JsonRequired] int? BatchCount);
public sealed record AgentQueueCapacityView(Guid? DeviceId, AgentQueueCapacityOverrides Overrides, string Version,
    AgentQueueCapacitySnapshot Effective, AgentQueueCapacitySnapshot Inherited);
public sealed record AgentQueueCapacityUpdate([property: JsonRequired] AgentQueueCapacityOverrides Overrides, [property: JsonRequired] string Version);

/// <summary>Desired capacity and the latest independently reported applied group.</summary>
public sealed record AgentQueueCapacityStatus(Guid DeviceId, AgentSettingsApplyState State, AgentQueueCapacitySnapshot Desired,
    AgentQueueCapacitySnapshot? Applied, DateTimeOffset? AppliedAt, DateTimeOffset? ReportedAt, AgentConfigurationFailure? Failure);
