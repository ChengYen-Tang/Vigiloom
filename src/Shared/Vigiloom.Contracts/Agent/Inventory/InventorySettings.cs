using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Interval between completed inventory passes, in seconds.</summary>
public sealed record InventorySettings([property: JsonRequired] int ScanIntervalSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CollectCommandLine = false)
{
    public const int MinimumIntervalSeconds = 900;
    public const int MaximumIntervalSeconds = 604800;
    public static InventorySettings Default { get; } = new(21600);

    public bool IsValid() => ScanIntervalSeconds is >= MinimumIntervalSeconds and <= MaximumIntervalSeconds;
}

/// <summary>Versioned independently of transport and metrics settings.</summary>
public sealed record InventorySettingsSnapshot([property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Revision, [property: JsonRequired] InventorySettings Settings)
{
    public const int CurrentSchema = 1;
    public const int ProcessSchema = 2;

    public static InventorySettingsSnapshot Create(InventorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid()) throw new ArgumentException("Invalid inventory settings.", nameof(settings));
        var schema = settings.CollectCommandLine ? ProcessSchema : CurrentSchema;
        return new(schema, RevisionFor(settings, schema), settings);
    }

    public bool IsValid() => Settings is not null && Settings.IsValid() &&
        SchemaVersion == (Settings.CollectCommandLine ? ProcessSchema : CurrentSchema) &&
        string.Equals(Revision, RevisionFor(Settings, SchemaVersion), StringComparison.Ordinal);

    private static string RevisionFor(InventorySettings settings, int schema)
    {
        Span<byte> bytes = stackalloc byte[2 * sizeof(int) + (schema == ProcessSchema ? 1 : 0)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, schema);
        BinaryPrimitives.WriteInt32BigEndian(bytes[sizeof(int)..], settings.ScanIntervalSeconds);
        if (schema == ProcessSchema) bytes[^1] = 1;
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

public sealed record InventorySettingsOverrides([property: JsonRequired] int? ScanIntervalSeconds, bool? CollectCommandLine = null);
public sealed record InventorySettingsView(Guid? DeviceId, InventorySettingsOverrides Overrides, string Version,
    InventorySettingsSnapshot Effective, InventorySettingsSnapshot Inherited);
public sealed record InventorySettingsUpdate([property: JsonRequired] InventorySettingsOverrides Overrides,
    [property: JsonRequired] string Version);
public sealed record InventorySettingsStatus(Guid DeviceId, AgentSettingsApplyState State,
    InventorySettingsSnapshot Desired, InventorySettingsSnapshot? Applied, DateTimeOffset? AppliedAt,
    DateTimeOffset? ReportedAt, AgentConfigurationFailure? Failure);

public interface IInventorySettingsReader
{
    Task<InventorySettingsSnapshot> ReadAsync(DbConnection connection, DbTransaction transaction,
        Guid deviceId, CancellationToken token);
}
