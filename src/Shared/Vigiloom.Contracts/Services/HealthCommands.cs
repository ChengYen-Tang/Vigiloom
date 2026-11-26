namespace Vigiloom.Contracts.Services;

public sealed record UpdateHealthNetworkCommand(HealthNetworkPolicyInput Input, Guid ActorId);
public sealed record CreateHealthTargetCommand(CreateHealthTargetInput Input, Guid ActorId);
public sealed record UpdateHealthTargetCommand(HealthTargetInput Input, Guid ActorId);
