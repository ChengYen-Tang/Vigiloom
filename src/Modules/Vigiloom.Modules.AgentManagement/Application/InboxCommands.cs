using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed record InboxCutoverCommand(string Version, Guid Actor);
public sealed record InboxPreviewCommand(AgentInboxPreviewRequest Input, Guid Actor);
public sealed record InboxUpdateCommand(AgentInboxSettingsUpdate Input, Guid Actor);
public sealed record ReplayPolicyCommand(AgentReplayPolicyUpdate Input, Guid Actor);
