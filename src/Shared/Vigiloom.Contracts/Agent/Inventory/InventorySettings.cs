using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Interval between completed inventory passes, in seconds.</summary>
public sealed record InventorySettings([property: JsonRequired] int ScanIntervalSeconds)
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

    public static InventorySettingsSnapshot Create(InventorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid()) throw new ArgumentException("Invalid inventory settings.", nameof(settings));
        return new(CurrentSchema, RevisionFor(settings), settings);
    }

    public bool IsValid() => SchemaVersion == CurrentSchema && Settings is not null && Settings.IsValid() &&
        string.Equals(Revision, RevisionFor(Settings), StringComparison.Ordinal);

    private static string RevisionFor(InventorySettings settings)
    {
        Span<byte> bytes = stackalloc byte[2 * sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, CurrentSchema);
        BinaryPrimitives.WriteInt32BigEndian(bytes[sizeof(int)..], settings.ScanIntervalSeconds);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

public sealed record InventorySettingsOverrides([property: JsonRequired] int? ScanIntervalSeconds);
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
