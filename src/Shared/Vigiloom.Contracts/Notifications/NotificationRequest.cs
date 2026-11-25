using System.Data.Common;

namespace Vigiloom.Contracts.Notifications;

/// <summary>A durable logical notification request, separate from delivery to any destination.</summary>
public sealed record NotificationRequest(Guid MessageId, string Source, DateTimeOffset OccurredAt, string Title, string Detail,
    Guid? ResourceId = null, Guid? EvidenceOwnerId = null, Guid? EvidenceId = null, Guid? RuleTestId = null);

/// <summary>Work type used to deliver a logical notification to Operations.</summary>
public static class NotificationWork
{
    public const string Kind = "notification.create.v1";
}

/// <summary>Records a notification request with its originating event in the same database transaction.</summary>
public interface INotificationWriter
{
    Task WriteAsync(NotificationRequest request, DbConnection connection, DbTransaction transaction, CancellationToken token);
    Task WriteBatchAsync(IReadOnlyList<NotificationRequest> requests, DbConnection connection, DbTransaction transaction, CancellationToken token);
}
