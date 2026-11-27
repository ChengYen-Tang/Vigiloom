namespace Vigiloom.Contracts.Operations;

public sealed record ResourcesAuditRetentionRequest(DateTimeOffset Cutoff, int BatchSize = 0);
