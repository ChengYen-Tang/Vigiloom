namespace Vigiloom.Contracts.Operations;

public sealed record SecurityDetectionRetentionRequest(DateTimeOffset Now, int Days, int BatchSize = 0);
