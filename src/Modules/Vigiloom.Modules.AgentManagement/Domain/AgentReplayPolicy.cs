using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class AgentInboxNotReadyException() : InvalidOperationException("The inbox cleanup protocol is not ready or its SQL clock moved backward.");

/// <summary>The central rollout state for the Agent replay window.</summary>
public sealed class AgentReplayPolicy
{
    public Guid Id { get; private set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public int Scope { get; private set; } = 1;
    public AgentReplayPhase Phase { get; private set; }
    public bool FreshInstallation { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? GraceUntil { get; private set; }
    public DateTimeOffset? EnforcedAt { get; private set; }
    /// <summary>SQL-clock cutover for the cleanup protocol; separate from replay-policy activation.</summary>
    public DateTimeOffset? CleanupCutoverAt { get; private set; }
    /// <summary>Fixed conservative deadline for receipts lacking a trusted SQL acceptance time.</summary>
    public DateTimeOffset? LegacyReceiptsRetainUntil { get; private set; }
    public int ReplayWindowSeconds { get; private set; } = 24 * 60 * 60;
    public int ReceiptRetentionDays { get; private set; } = 7;
    /// <summary>Most recent central configuration decision, used to detect SQL clock rollback.</summary>
    public DateTimeOffset? InboxSettingsChangedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public bool InitializeFresh(DateTimeOffset now)
    {
        if (Phase != AgentReplayPhase.Disabled) return false;
        ActivatedAt = now.ToUniversalTime();
        EnforcedAt = ActivatedAt;
        FreshInstallation = true;
        Phase = AgentReplayPhase.Enforced;
        return true;
    }

    public bool Activate(DateTimeOffset now)
    {
        if (Phase != AgentReplayPhase.Disabled) return false;
        ActivatedAt = now.ToUniversalTime();
        GraceUntil = ActivatedAt.Value.AddHours(24).AddMinutes(5);
        Phase = AgentReplayPhase.Grace;
        return true;
    }

    public bool Enforce(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        if (Phase != AgentReplayPhase.Grace || GraceUntil is null || utc < GraceUntil) return false;
        EnforcedAt = utc;
        Phase = AgentReplayPhase.Enforced;
        return true;
    }

    /// <summary>The caller must first stop and drain every receiver that does not support the cleanup protocol.</summary>
    public bool BeginCleanupCutover(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        if (Phase != AgentReplayPhase.Enforced || EnforcedAt is null || utc < EnforcedAt || CleanupCutoverAt is not null)
            return false;
        CleanupCutoverAt = utc;
        LegacyReceiptsRetainUntil = utc.AddDays(ReceiptRetentionDays);
        InboxSettingsChangedAt = utc;
        return true;
    }

    /// <summary>Creates the prior replay obligation when a validated central window is shortened.</summary>
    public AgentReplayGeneration? ChangeInboxSettings(int replaySeconds, int retentionDays, DateTimeOffset now)
    {
        if (!AgentInboxPolicyLimits.IsValid(replaySeconds, retentionDays)) throw new ArgumentException("Invalid replay window or receipt retention.");
        var utc = now.ToUniversalTime();
        if (Phase != AgentReplayPhase.Enforced || CleanupCutoverAt is null || InboxSettingsChangedAt is null ||
            utc < CleanupCutoverAt || utc < InboxSettingsChangedAt)
            throw new AgentInboxNotReadyException();
        AgentReplayGeneration? previous = null;
        if (replaySeconds < ReplayWindowSeconds)
        {
            var deadline = utc.AddSeconds((long)ReplayWindowSeconds + AgentInboxPolicyLimits.FutureSkewSeconds);
            previous = new AgentReplayGeneration(Id, ReplayWindowSeconds, deadline);
        }
        ReplayWindowSeconds = replaySeconds;
        ReceiptRetentionDays = retentionDays;
        InboxSettingsChangedAt = utc;
        return previous;
    }
}
