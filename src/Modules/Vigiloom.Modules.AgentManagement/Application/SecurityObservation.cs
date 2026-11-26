using Vigiloom.Contracts.Resources;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed record SecurityAgentObservation(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset? LastHeartbeatAt,
    int HeartbeatSafetyIntervalSeconds, Guid? QueueReportEpoch, long? QueueReportSequence,
    Guid? InventoryConfigurationEpoch, long? InventoryConfigurationSequence, DateTimeOffset? InventoryConfigurationReportedAt, string? InventoryConfigurationJson,
    Guid? IntegrityConfigurationEpoch, long? IntegrityConfigurationSequence, DateTimeOffset? IntegrityConfigurationReportedAt, string? IntegrityConfigurationJson,
    DateTimeOffset? BaselineConfigurationReportedAt, string? BaselineConfigurationJson);

public sealed record SecurityObservation(DeviceAgentState Device, SecurityAgentObservation? Agent);

public interface IHostSecurityObservationReader
{
    Task<SecurityObservation?> GetAsync(Guid deviceId, CancellationToken token);
}
