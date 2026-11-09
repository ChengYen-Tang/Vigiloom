namespace Vigiloom.Modules.AlertingOperations.Application;

// Stored kind values are durable wire formats; add a new value for incompatible changes.
internal static class AuditKinds
{
    public const short SpeedSettingsChangedV1 = 1;
    public const short PingSettingsChangedV1 = 2;
    public const short AgentReplayPolicyChangedV1 = 3;
    public const short ServiceApprovedV1 = 4;
    public const short ServiceSourceChangedV1 = 5;
    public const short GroupChangedV1 = 6;
    public const short InstancePolicyChangedV1 = 7;
    public const short ServiceInstanceChangedV1 = 8;
    public const short ServiceCollectionChangedV1 = 9;
    public const short LogQuotaChangedV1 = 10;
    public const short TraceQuotaChangedV1 = 11;
    public const short CollectorChangedV1 = 12;
    public const short HealthTargetChangedV1 = 13;
    public const short HealthNetworkPolicyChangedV1 = 14;
    public const short SpeedRetentionChangedV1 = 15;
    public const short AgentInboxSettingsChangedV1 = 16;
    public const short AgentInboxCleanupActivatedV1 = 17;
}
