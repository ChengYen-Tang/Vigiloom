namespace Vigiloom.Agent.Core.Identity;

/// <summary>Private durable Agent enrollment and rotation state. Never log or expose this object.</summary>
public sealed class IdentityState
{
    public int Version { get; set; } = 1;
    public string Server { get; set; } = "";
    public Guid? AgentId { get; set; }
    public string? EnrollmentSecret { get; set; }
    public string MachineSecret { get; set; } = "";
    public string? PendingSecret { get; set; }
}

/// <summary>Last valid configuration saved with the identity that owns it.</summary>
public sealed record AgentAppliedSettings(Vigiloom.Contracts.Agent.AgentSettingsSnapshot Snapshot, DateTimeOffset AppliedAt, Guid SaveToken, Guid AgentId, string Server);

/// <summary>Last valid independent queue capacity group saved for one Agent and server.</summary>
public sealed record AgentAppliedQueueCapacity(Vigiloom.Contracts.Agent.AgentQueueCapacitySnapshot Snapshot, DateTimeOffset AppliedAt, Guid SaveToken, Guid AgentId, string Server);

/// <summary>Last valid metric policy bound to the registered Agent identity.</summary>
public sealed record AgentSavedMetricSettings(Vigiloom.Contracts.Agent.AgentMetricSettingsSnapshot Snapshot, DateTimeOffset SavedAt, Guid SaveToken, Guid AgentId, string Server);
public sealed record AgentSavedInventorySettings(Vigiloom.Contracts.Agent.Inventory.InventorySettingsSnapshot Snapshot, DateTimeOffset SavedAt, Guid SaveToken, Guid AgentId, string Server);
public sealed record AgentSavedLogSettings(Vigiloom.Contracts.Agent.AgentLogSettingsSnapshot Snapshot, DateTimeOffset SavedAt, Guid SaveToken, Guid AgentId, string Server);
public sealed record AgentSavedIntegritySettings(Vigiloom.Contracts.Agent.Integrity.IntegritySettingsSnapshot Snapshot, DateTimeOffset SavedAt, Guid SaveToken, Guid AgentId, string Server);
