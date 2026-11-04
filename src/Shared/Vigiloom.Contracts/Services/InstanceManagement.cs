namespace Vigiloom.Contracts.Services;

public sealed record InstancePolicySnapshot(int PerService, int Total, string Version);
public sealed record InstancePolicyInput(int PerService, int Total, string Version);
public sealed record ServiceInstanceSummary(Guid Id, Guid ServiceId, string ExternalInstanceId,
    DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt, string Version);
public sealed record ServiceInstanceUpdate(string Version);
public sealed record ManagedServiceSummary(Guid Id, string Name, string Environment, Guid SourceId, DateTimeOffset? StoppedAt, string Version);
