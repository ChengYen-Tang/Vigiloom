namespace Vigiloom.Modules.AlertingOperations.Application;

// Stored kind values are durable wire formats; add a new value for incompatible changes.
internal static class AuditKinds
{
    public const short SpeedSettingsChangedV1 = 1;
    public const short PingSettingsChangedV1 = 2;
    public const short AgentReplayPolicyChangedV1 = 3;
}
