namespace Vigiloom.Contracts.Agent;

/// <summary>The central transition state for the Agent replay-window rollout.</summary>
public enum AgentReplayPhase { Disabled = 0, Grace = 1, Enforced = 2 }

public sealed record AgentReplayPolicyView(AgentReplayPhase Phase, DateTimeOffset? ActivatedAt,
    DateTimeOffset? GraceUntil, DateTimeOffset? EnforcedAt, string Version);

public sealed record AgentReplayPolicyUpdate(AgentReplayPhase Target, string Version, bool ConfirmedReceiversUpgraded);
