namespace Vigiloom.Contracts.Events;

/// <summary>A reference to an existing event or detection; original evidence remains with its source.</summary>
public sealed record EventTimelineEntry(string Source, DateTimeOffset Time, Guid ResourceId, string ResourceKind,
    string Summary, string Evidence, string Link, string? RuleLink = null);

public sealed record EventTimelinePage(IReadOnlyList<EventTimelineEntry> Items, int Page, bool HasMore, bool LimitReached);

public sealed record EventTimelineQuery(DateTimeOffset From, DateTimeOffset Until, Guid? GroupId = null, string? Source = null, int Page = 1);
