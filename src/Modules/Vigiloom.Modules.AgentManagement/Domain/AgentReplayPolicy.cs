using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

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
}
