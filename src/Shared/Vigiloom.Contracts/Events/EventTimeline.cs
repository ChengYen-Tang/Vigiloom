namespace Vigiloom.Contracts.Events;

/// <summary>A reference to an existing event or detection; original evidence remains with its source.</summary>
public sealed record EventTimelineEntry(string Source, DateTimeOffset Time, Guid ResourceId, string ResourceKind,
    string Summary, string Evidence, string Link, string? RuleLink = null);

public sealed record EventTimelinePage(IReadOnlyList<EventTimelineEntry> Items, int Page, bool HasMore, bool LimitReached);

public sealed record EventTimelineQuery(DateTimeOffset From, DateTimeOffset Until, Guid? GroupId = null, string? Source = null, int Page = 1);

/// <summary>One stable event summary delivered to the Operations timeline.</summary>
public sealed record EventTimelineDelivery(Guid MessageId, EventTimelineEntry Entry);

/// <summary>Temporal work type for a timeline summary.</summary>
public static class EventTimelineWork
{
    public const string Kind = "event-timeline.append.v1";
}
