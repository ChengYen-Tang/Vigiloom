namespace Vigiloom.Contracts.Notifications;

/// <summary>A generated notification request. Delivery is tracked separately by its channel.</summary>
public sealed record NotificationView(Guid Id, Guid MessageId, string Source, DateTimeOffset OccurredAt, string Title, string Detail,
    Guid? ResourceId, Guid? EvidenceOwnerId, Guid? EvidenceId, Guid? RuleTestId);

public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, int Page, bool HasMore);
public sealed record NotificationQuery(int Page, string? Source = null, DateOnly? Start = null, DateOnly? End = null);

public interface INotificationReader
{
    Task<NotificationPage> GetRecentAsync(NotificationQuery query, CancellationToken token);
}
