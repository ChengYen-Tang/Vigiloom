namespace Vigiloom.Contracts.Operations;

public sealed record SecurityAuditRetentionRequest(DateTimeOffset Cutoff, int BatchSize = 0);
public sealed record SecurityDetectionRetentionRequest(DateTimeOffset Now, int Days, int BatchSize = 0);
