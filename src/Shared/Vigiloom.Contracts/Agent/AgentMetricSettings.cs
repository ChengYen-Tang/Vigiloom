using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>Collection intervals for host and process measurements, in seconds.</summary>
public sealed record AgentMetricSettings(
    [property: JsonRequired] int HostIntervalSeconds,
    [property: JsonRequired] int ProcessIntervalSeconds,
    [property: JsonRequired] int CpuMillisecondsPerMinute = 3000,
    [property: JsonRequired] int WorkingSetLimitMiB = 512,
    [property: JsonRequired] int ProcessCollectionBudgetMs = 2000,
    [property: JsonRequired] int UploadLimitMiBPerMinute = 16,
    [property: JsonRequired] int IoLimitMiBPerMinute = 64)
{
    public static AgentMetricSettings Default { get; } = new(30, 60);

    public bool IsValid() => HostIntervalSeconds is >= 10 and <= 3600 && ProcessIntervalSeconds is >= 10 and <= 3600 && CpuMillisecondsPerMinute is >= 100 and <= 60000 && WorkingSetLimitMiB is >= 64 and <= 4096 && ProcessCollectionBudgetMs is >= 100 and <= 30000 && UploadLimitMiBPerMinute is >= 2 and <= 1024 && IoLimitMiBPerMinute is >= 1 and <= 4096;
}

/// <summary>Independently versioned metric collection settings.</summary>
public sealed record AgentMetricSettingsSnapshot(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Revision,
    [property: JsonRequired] AgentMetricSettings Settings)
{
    public const int CurrentSchema = 1;

    public static AgentMetricSettingsSnapshot Create(AgentMetricSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid()) throw new ArgumentException("Invalid Agent metric settings.", nameof(settings));
        return new(CurrentSchema, ComputeRevision(settings), settings);
    }

    public bool IsValid() => SchemaVersion == CurrentSchema && Settings is not null && Settings.IsValid() &&
        string.Equals(Revision, ComputeRevision(Settings), StringComparison.Ordinal);

    /// <summary>Recognizes audit revisions created before the CPU field was introduced.</summary>
    public static bool MatchesLegacyAuditRevision(AgentMetricSettings settings, string revision, bool cpuOverrideAbsent, bool memoryOverrideAbsent, bool processBudgetOverrideAbsent, bool uploadOverrideAbsent)
    {
        if (settings.IoLimitMiBPerMinute != 64) return false;
        Span<byte> bytes = stackalloc byte[7 * sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, CurrentSchema);
        BinaryPrimitives.WriteInt32BigEndian(bytes[4..], settings.HostIntervalSeconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes[8..], settings.ProcessIntervalSeconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], settings.CpuMillisecondsPerMinute);
        BinaryPrimitives.WriteInt32BigEndian(bytes[16..], settings.WorkingSetLimitMiB);
        BinaryPrimitives.WriteInt32BigEndian(bytes[20..], settings.ProcessCollectionBudgetMs);
        BinaryPrimitives.WriteInt32BigEndian(bytes[24..], settings.UploadLimitMiBPerMinute);
        return string.Equals(revision, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal) ||
            uploadOverrideAbsent && settings.UploadLimitMiBPerMinute == 16 &&
            (string.Equals(revision, Convert.ToHexStringLower(SHA256.HashData(bytes[..24])), StringComparison.Ordinal) ||
            processBudgetOverrideAbsent && settings.ProcessCollectionBudgetMs == 2000 &&
            (string.Equals(revision, Convert.ToHexStringLower(SHA256.HashData(bytes[..20])), StringComparison.Ordinal) ||
            memoryOverrideAbsent && settings.WorkingSetLimitMiB == 512 &&
            (string.Equals(revision, Convert.ToHexStringLower(SHA256.HashData(bytes[..16])), StringComparison.Ordinal) ||
            cpuOverrideAbsent && settings.CpuMillisecondsPerMinute == 3000 &&
            string.Equals(revision, Convert.ToHexStringLower(SHA256.HashData(bytes[..12])), StringComparison.Ordinal))));
    }

    private static string ComputeRevision(AgentMetricSettings settings)
    {
        Span<byte> bytes = stackalloc byte[8 * sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, CurrentSchema);
        BinaryPrimitives.WriteInt32BigEndian(bytes[4..], settings.HostIntervalSeconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes[8..], settings.ProcessIntervalSeconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], settings.CpuMillisecondsPerMinute);
        BinaryPrimitives.WriteInt32BigEndian(bytes[16..], settings.WorkingSetLimitMiB);
        BinaryPrimitives.WriteInt32BigEndian(bytes[20..], settings.ProcessCollectionBudgetMs);
        BinaryPrimitives.WriteInt32BigEndian(bytes[24..], settings.UploadLimitMiBPerMinute);
        BinaryPrimitives.WriteInt32BigEndian(bytes[28..], settings.IoLimitMiBPerMinute);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

/// <summary>Null intervals inherit the platform or built-in policy.</summary>
public sealed record AgentMetricSettingsOverrides([property: JsonRequired] int? HostIntervalSeconds,
    [property: JsonRequired] int? ProcessIntervalSeconds, [property: JsonRequired] int? CpuMillisecondsPerMinute = null,
    [property: JsonRequired] int? WorkingSetLimitMiB = null, [property: JsonRequired] int? ProcessCollectionBudgetMs = null,
    [property: JsonRequired] int? UploadLimitMiBPerMinute = null, [property: JsonRequired] int? IoLimitMiBPerMinute = null);
public sealed record AgentMetricSettingsView(Guid? DeviceId, AgentMetricSettingsOverrides Overrides, string Version,
    AgentMetricSettingsSnapshot Effective, AgentMetricSettingsSnapshot Inherited);
public sealed record AgentMetricSettingsUpdate([property: JsonRequired] AgentMetricSettingsOverrides Overrides,
    [property: JsonRequired] string Version);
