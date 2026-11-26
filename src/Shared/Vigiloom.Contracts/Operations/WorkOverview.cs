namespace Vigiloom.Contracts.Operations;

/// <summary>A recent background job or message result.</summary>
public sealed record WorkEntry(string Name, string Result, DateTimeOffset? OccurredAt, string? Detail, string? Reference = null);

/// <summary>Shared Hangfire activity and this service's outbox status.</summary>
public sealed record WorkOverview(long Workers, long Queued, long Processing, long Failed, long Succeeded, int PendingMessages, int FailedMessages, IReadOnlyList<WorkEntry> Recent);
