namespace Vigiloom.Contracts.Services;

public sealed record UpdateInstancePolicyCommand(InstancePolicyInput Input, Guid ActorId);
public sealed record ChangeInstanceCommand(string Version, bool Retired, Guid ActorId);
