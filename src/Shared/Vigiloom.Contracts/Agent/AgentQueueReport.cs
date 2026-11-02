using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>Current non-secret queue pressure. A null snapshot means it could not be read.</summary>
public sealed record AgentHeartbeatReport([property: JsonRequired] int Version, [property: JsonRequired] Guid Epoch, [property: JsonRequired] long Sequence,
    [property: JsonRequired] AgentQueueSnapshot? Queue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentConfigurationReport? Configuration = null);

/// <summary>Agent-observed transport settings status; null applied fields mean no remote settings have been applied.</summary>
public sealed record AgentConfigurationReport(
    [property: JsonRequired] AgentSettingsSnapshot? Applied,
    [property: JsonRequired] DateTimeOffset? AppliedAt,
    [property: JsonRequired] AgentConfigurationFailure? Failure)
{
    public bool IsValid()
    {
        if (Failure is { } failure && !Enum.IsDefined(failure)) return false;
        if (Applied is null) return AppliedAt is null;
        return Applied.IsValid() && AppliedAt is not null;
    }
}

public enum AgentConfigurationFailure { Unavailable = 1, Invalid = 2, Unauthorized = 3, LocalStorage = 4, Conflict = 5 }

/// <summary>Current queue-report session for a registered Agent.</summary>
public sealed record AgentQueueSession(Guid? Epoch);

/// <summary>Atomically replace an observed queue-report session with an idempotent new session ID.</summary>
public sealed record AgentQueueSessionRequest([property: JsonRequired] Guid? ExpectedEpoch, [property: JsonRequired] Guid Epoch);

/// <summary>Logical payload capacity and counts, including retained quarantined batches.</summary>
public sealed record AgentQueueSnapshot(
    [property: JsonRequired] long TotalBytes,
    [property: JsonRequired] long TotalBatches,
    [property: JsonRequired] long QuarantinedBatches,
    [property: JsonRequired] long CapacityBytes,
    [property: JsonRequired] int CapacityBatches)
{
    public bool IsValid() => TotalBytes >= 0 && TotalBatches >= 0 && QuarantinedBatches >= 0 &&
        QuarantinedBatches <= TotalBatches && CapacityBytes > 0 && CapacityBatches > 0;
}
