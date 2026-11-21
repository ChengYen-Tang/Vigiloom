using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

public sealed class NotificationReader(IDbContextFactory<OperationsDbContext> contexts) : INotificationReader
{
    public async Task<AlertEpisodePage> GetEpisodesAsync(NotificationQuery query, CancellationToken token)
    {
        if (query.Page is < 1 or > 1000 || query.Source is { Length: > 64 } || query.Start > query.End)
            throw new ArgumentOutOfRangeException(nameof(query));
        const int pageSize = 50;
        await using var db = await contexts.CreateDbContextAsync(token);
        var ping = db.PingAlerts.AsNoTracking().Where(x => query.Source == null || query.Source == "" || query.Source == "ping")
            .Select(x => new { x.Id, Source = "ping", ResourceId = x.DeviceId, Target = (string?)x.Target,
                CheckId = (Guid?)null, x.OpenedAt, x.RecoveredAt, State = (int?)null, x.RowId });
        var service = db.ServiceHealthAlerts.AsNoTracking().Where(x => query.Source == null || query.Source == "" || query.Source == "service-health")
            .Select(x => new { x.Id, Source = "service-health", ResourceId = x.ServiceId, Target = (string?)null,
                CheckId = (Guid?)x.TargetId, x.OpenedAt, x.RecoveredAt, State = (int?)x.State, x.RowId });
        var episodes = ping.Concat(service);
        if (query.Start is { } start)
            episodes = episodes.Where(x => x.OpenedAt >= new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (query.End is { } end && end != DateOnly.MaxValue)
            episodes = episodes.Where(x => x.OpenedAt < new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var rows = await episodes.OrderByDescending(x => x.OpenedAt).ThenByDescending(x => x.RowId).ThenBy(x => x.Source)
            .Skip((query.Page - 1) * pageSize).Take(pageSize + 1).ToArrayAsync(token);
        return new(rows.Take(pageSize).Select(x => new AlertEpisode(x.Id, x.Source, x.ResourceId, x.Target, x.CheckId,
            x.OpenedAt, x.RecoveredAt, x.State)).ToArray(), query.Page, rows.Length > pageSize && query.Page < 1000);
    }

    public async Task<NotificationPage> GetRecentAsync(NotificationQuery query, CancellationToken token)
    {
        if (query.Page is < 1 or > 1000 || query.Source is { Length: > 64 } || query.Start > query.End)
            throw new ArgumentOutOfRangeException(nameof(query));
        const int pageSize = 50;
        await using var db = await contexts.CreateDbContextAsync(token);
        var selected = db.Notifications.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Source)) selected = selected.Where(x => x.Source == query.Source);
        if (query.Start is { } start)
        {
            var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            selected = selected.Where(x => x.OccurredAt >= from);
        }
        if (query.End is { } end && end != DateOnly.MaxValue)
        {
            var until = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            selected = selected.Where(x => x.OccurredAt < until);
        }
        var rows = await selected.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((query.Page - 1) * pageSize).Take(pageSize + 1)
            .Select(x => new NotificationView(x.Id, x.MessageId, x.Source, x.OccurredAt, x.Title, x.Detail,
                x.ResourceId, x.EvidenceOwnerId, x.EvidenceId, x.RuleTestId)).ToArrayAsync(token);
        var retentionDays = await db.AuditRetentionSettings.AsNoTracking().Select(x => (int?)x.Days).SingleOrDefaultAsync(token) ?? 365;
        return new(rows.Take(pageSize).ToArray(), query.Page, rows.Length > pageSize && query.Page < 1000, retentionDays);
    }
}
