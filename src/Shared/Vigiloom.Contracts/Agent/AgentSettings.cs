using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>Resolved transport scheduling settings. Other capability groups have separate revisions.</summary>
public sealed record AgentSettings(
    [property: JsonRequired] int HeartbeatSeconds,
    [property: JsonRequired] int SettingsPollSeconds,
    [property: JsonRequired] int BatchIntervalSeconds)
{
    public static AgentSettings Default { get; } = new(30, 300, 5);

    public bool IsValid() => HeartbeatSeconds is >= 10 and <= 300 &&
        SettingsPollSeconds is >= 60 and <= 3600 && BatchIntervalSeconds is >= 1 and <= 300;
}

/// <summary>Stable version of an effective configuration, independent of SQL row versions.</summary>
public sealed record AgentSettingsSnapshot(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Revision,
    [property: JsonRequired] AgentSettings Settings)
{
    public const int CurrentSchema = 1;

    public static AgentSettingsSnapshot Create(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid()) throw new ArgumentException("Invalid Agent settings.", nameof(settings));
        return new(CurrentSchema, ComputeRevision(settings), settings);
    }

    public bool IsValid() => SchemaVersion == CurrentSchema && Settings is not null && Settings.IsValid() &&
        string.Equals(Revision, ComputeRevision(Settings), StringComparison.Ordinal);

    private static string ComputeRevision(AgentSettings settings)
    {
        Span<byte> bytes = stackalloc byte[4 * sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, CurrentSchema);
        BinaryPrimitives.WriteInt32BigEndian(bytes[4..], settings.HeartbeatSeconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes[8..], settings.SettingsPollSeconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], settings.BatchIntervalSeconds);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
