using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>Explicit values for one layer. Null restores inheritance for that field.</summary>
public sealed record AgentSettingsOverrides(
    [property: JsonRequired] int? HeartbeatSeconds,
    [property: JsonRequired] int? SettingsPollSeconds,
    [property: JsonRequired] int? BatchIntervalSeconds);

/// <summary>Administrative view of one layer and the effective transport settings.</summary>
public sealed record AgentSettingsView(Guid? DeviceId, AgentSettingsOverrides Overrides, string Version, AgentSettingsSnapshot Effective, AgentSettingsSnapshot Inherited);

/// <summary>Optimistic version is empty when the layer has never been saved.</summary>
public sealed record AgentSettingsUpdate([property: JsonRequired] AgentSettingsOverrides Overrides, [property: JsonRequired] string Version);

public sealed record AgentSettingsCommand(AgentSettingsUpdate Input, Guid Actor);

public interface IAgentSettings
{
    Task<AgentSettingsView?> GetAsync(Guid? deviceId, CancellationToken token);
    Task<AgentSettingsView?> UpdateAsync(Guid? deviceId, AgentSettingsUpdate update, Guid actorId, CancellationToken token);
}
