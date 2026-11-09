namespace Vigiloom.Contracts.Agent;

/// <summary>Bounds for the central replay and inbox-retention policy.</summary>
public static class AgentInboxPolicyLimits
{
    public const int MinReplaySeconds = 1;
    public const int MaxReplaySeconds = 30 * 24 * 60 * 60;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
    public const int FutureSkewSeconds = 5 * 60;

    public static bool IsValid(int replaySeconds, int retentionDays) =>
        replaySeconds is >= MinReplaySeconds and <= MaxReplaySeconds &&
        retentionDays is >= MinRetentionDays and <= MaxRetentionDays &&
        (long)retentionDays * 24 * 60 * 60 >= (long)replaySeconds + FutureSkewSeconds;
}
