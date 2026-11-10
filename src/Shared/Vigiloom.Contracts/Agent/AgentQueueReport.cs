using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>Current non-secret queue pressure. A null snapshot means it could not be read.</summary>
public sealed record AgentHeartbeatReport([property: JsonRequired] int Version, [property: JsonRequired] Guid Epoch, [property: JsonRequired] long Sequence,
    [property: JsonRequired] AgentQueueSnapshot? Queue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentConfigurationReport? Configuration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentQuarantineBreakdown? Quarantine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentQueueCapacityReport? CapacityConfiguration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentMetricLossReport? MetricLoss = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentLogStatusReport? LogStatus = null)
{
    public bool IsValid() => Version == 1 && Epoch.Version == 7 && Sequence >= 1 &&
        (Queue is null || Queue.IsValid()) && (Configuration is null || Configuration.IsValid()) &&
        (Quarantine is null || Queue is not null && Quarantine.IsValid(Queue.QuarantinedBatches)) && (MetricLoss is null || MetricLoss.IsValid()) &&
        (LogStatus is null || LogStatus.IsValid()) &&
        (CapacityConfiguration is null || CapacityConfiguration.IsValid() &&
            (CapacityConfiguration.Applied is null || Queue is not null &&
                Queue.CapacityBytes == CapacityConfiguration.Applied.Capacity.PayloadBytes &&
                Queue.CapacityBatches == CapacityConfiguration.Applied.Capacity.BatchCount));
}

/// <summary>Cumulative confirmed losses since ledger creation; unknown completeness is separate from the count.</summary>
public sealed record AgentMetricLossReport([property: JsonRequired] Guid LedgerId, [property: JsonRequired] DateTimeOffset StartedAt,
    [property: JsonRequired] long Batches, [property: JsonRequired] long Records, [property: JsonRequired] bool CompletenessUnknown)
{
    public bool IsValid() => LedgerId.Version == 7 && StartedAt != default && StartedAt.Offset == TimeSpan.Zero &&
        Batches >= 0 && Records >= Batches;
}

/// <summary>Applied state of the independently versioned queue capacity group.</summary>
public sealed record AgentQueueCapacityReport([property: JsonRequired] AgentQueueCapacitySnapshot? Applied,
    [property: JsonRequired] DateTimeOffset? AppliedAt, [property: JsonRequired] AgentConfigurationFailure? Failure)
{
    public bool IsValid() => (Failure is null || Enum.IsDefined(Failure.Value)) &&
        (Applied is null ? AppliedAt is null : Applied.IsValid() && AppliedAt is not null);
}

/// <summary>Counts of retained batches by fixed rejection reason, not discarded or lost data.</summary>
public sealed record AgentQuarantineBreakdown([property: JsonRequired] long InvalidLocalBatch, [property: JsonRequired] long BadRequest,
    [property: JsonRequired] long Conflict, [property: JsonRequired] long PayloadTooLarge,
    [property: JsonRequired] long UnsupportedMediaType, [property: JsonRequired] long Expired, [property: JsonRequired] long Other)
{
    public bool IsValid(long total)
    {
        if (total < 0) return false;
        foreach (var value in (ReadOnlySpan<long>)[InvalidLocalBatch, BadRequest, Conflict, PayloadTooLarge, UnsupportedMediaType, Expired, Other])
        {
            if (value < 0 || value > total) return false;
            total -= value;
        }
        return total == 0;
    }
}

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
