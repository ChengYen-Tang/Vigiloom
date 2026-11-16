using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Persists a logical notification; channel selection and delivery happen separately.</summary>
public sealed class NotificationWriter : INotificationWriter
{
    public Task WriteAsync(NotificationRequest request, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
        WriteBatchAsync([request], connection, transaction, token);

    public async Task WriteBatchAsync(IReadOnlyList<NotificationRequest> requests, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        if (requests.Count == 0) return;
        if (requests.Any(x => x.MessageId.Version != 7)) throw new ArgumentException("Notification message IDs must be UUID v7.", nameof(requests));
        var unique = new Dictionary<Guid, NotificationRequest>();
        foreach (var request in requests)
        {
            if (unique.TryGetValue(request.MessageId, out var prior) && prior != request)
                throw new InvalidOperationException("Different notifications use the same message ID.");
            unique[request.MessageId] = request;
        }
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var ids = unique.Keys.ToArray();
        var existing = await db.Notifications.AsNoTracking().Where(x => ids.Contains(x.MessageId)).ToArrayAsync(token);
        foreach (var item in existing)
        {
            var request = unique[item.MessageId];
            if (item.Source != request.Source || item.OccurredAt != request.OccurredAt ||
                item.Title != request.Title || item.Detail != request.Detail || item.ResourceId != request.ResourceId ||
                item.EvidenceOwnerId != request.EvidenceOwnerId || item.EvidenceId != request.EvidenceId || item.RuleTestId != request.RuleTestId)
                throw new InvalidOperationException("A different notification already uses this message ID.");
            unique.Remove(item.MessageId);
        }
        foreach (var request in unique.Values) Add(db, request);
        await db.SaveChangesAsync(token);
    }

    internal static void Add(OperationsDbContext db, NotificationRequest request) => db.Notifications.Add(new Notification
    {
        MessageId = request.MessageId, Source = request.Source, OccurredAt = request.OccurredAt,
        Title = request.Title, Detail = request.Detail, ResourceId = request.ResourceId,
        EvidenceOwnerId = request.EvidenceOwnerId, EvidenceId = request.EvidenceId, RuleTestId = request.RuleTestId
    });
}
