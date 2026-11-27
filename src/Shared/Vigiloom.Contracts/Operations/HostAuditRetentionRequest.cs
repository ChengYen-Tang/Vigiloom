namespace Vigiloom.Contracts.Operations;

public sealed record HostAuditRetentionRequest(DateTimeOffset Cutoff, int BatchSize = 0);
