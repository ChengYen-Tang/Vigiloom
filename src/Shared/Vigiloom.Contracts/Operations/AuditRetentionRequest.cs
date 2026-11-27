namespace Vigiloom.Contracts.Operations;

public sealed record AuditRetentionRequest(DateTimeOffset Cutoff, int BatchSize = 0, string? ExcludedOperationsDatabase = null);
