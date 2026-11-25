using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Operations;

/// <summary>A validated Agent's host logs, forwarded by the host monitoring service.</summary>
public sealed record HostLogSubmission(Guid AgentId, Guid DeviceId, long DeviceGeneration, AgentLogBatch Batch);

public enum HostLogReceiptStatus { Accepted, Invalid, Conflict, RateLimited, BatchTooLarge, Busy }

/// <summary>The result of saving a host Log submission.</summary>
public sealed record HostLogReceipt(HostLogReceiptStatus Status, TimeSpan? RetryAfter = null);
