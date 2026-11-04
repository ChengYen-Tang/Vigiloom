namespace Vigiloom.Contracts.Operations;

/// <summary>Log record budgets and optimistic-concurrency version.</summary>
public sealed record LogQuotaSettings(int SourceRate, int SourceBurst, int GlobalRate, int GlobalBurst, string Version);
