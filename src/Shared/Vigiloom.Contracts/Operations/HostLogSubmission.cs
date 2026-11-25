using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Operations;

/// <summary>A validated Agent's host logs, forwarded by the host monitoring service.</summary>
public sealed record HostLogSubmission(Guid AgentId, Guid DeviceId, long DeviceGeneration, AgentLogBatch Batch);

/// <summary>Identifies committed host events required by an internal consumer.</summary>
public sealed record HostLogReadRequest(Guid AgentId, IReadOnlyCollection<Guid> MessageIds);

public enum LogReceiptStatus { Accepted, Invalid, Conflict, RateLimited, BatchTooLarge, Busy }

/// <summary>The result of saving a Log submission.</summary>
public sealed record LogReceipt(LogReceiptStatus Status, TimeSpan? RetryAfter = null);
