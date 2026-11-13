using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Scheduling.Persistence;

namespace Vigiloom.Infrastructure.Scheduling;

/// <summary>Projects safe schedule and dashboard audit metadata owned by scheduling.</summary>
public enum SchedulingAuditAction { Unknown, SettingsChanged, Delete, Requeue, Enqueue, RecurringRemove, RecurringTrigger }
public enum SchedulingAuditOutcome { Unknown, Started, Accepted, Pending, Rejected }
public sealed record SchedulingAuditEntry(DateTimeOffset OccurredAt, Guid ActorId, SchedulingAuditAction Action, SchedulingAuditOutcome Outcome, string? Reference);
public sealed record SchedulingAuditPage(IReadOnlyList<SchedulingAuditEntry> Items, int Page, bool HasMore, bool LimitReached);

public sealed class SchedulingAuditFeed(IDbContextFactory<SchedulingDbContext> contexts)
{
    private const int PageSize = 50;
    public async Task<SchedulingAuditPage> QueryAsync(DateTimeOffset from, DateTimeOffset to, int page, CancellationToken token)
    {
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || to <= from || to - from > TimeSpan.FromDays(31) || page is < 1 or > 1000)
            throw new ArgumentException("Invalid audit range.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.DashboardAudit.AsNoTracking().Where(x => x.OccurredAt >= from && x.OccurredAt < to)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new { x.OccurredAt, x.ActorId, x.Action, x.Result, x.JobId }).ToArrayAsync(token);
        var entries = rows.Take(PageSize).Select(row =>
        {
            var action = Action(row.Action);
            var reference = (action is SchedulingAuditAction.Delete or SchedulingAuditAction.Requeue or SchedulingAuditAction.Enqueue) &&
                long.TryParse(row.JobId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id.ToString(CultureInfo.InvariantCulture)
                : (action is SchedulingAuditAction.RecurringRemove or SchedulingAuditAction.RecurringTrigger) &&
                    (row.JobId is "vigiloom-outbox-dispatch" or "vigiloom-speed-test" or "vigiloom-speed-retention" or "vigiloom-agent-inbox-retention" or "vigiloom-inventory-expiry" or "vigiloom-ping-rollup-backfill") ? row.JobId : null;
            return new SchedulingAuditEntry(row.OccurredAt, row.ActorId, action, Outcome(row.Result), reference);
        }).ToArray();
        var more = rows.Length > PageSize;
        return new(entries, page, more && page < 1000, more && page == 1000);
    }

    private static SchedulingAuditAction Action(string action) => action.ToLowerInvariant() switch
    {
        "/settings/jobs/schedule" => SchedulingAuditAction.SettingsChanged,
        "/jobs/enqueued/delete" or "/jobs/processing/delete" or "/jobs/scheduled/delete" or "/jobs/failed/delete" or
            "/jobs/awaiting/delete" or "/jobs/actions/delete" => SchedulingAuditAction.Delete,
        "/jobs/enqueued/requeue" or "/jobs/processing/requeue" or "/jobs/succeeded/requeue" or "/jobs/failed/requeue" or
            "/jobs/deleted/requeue" or "/jobs/actions/requeue" => SchedulingAuditAction.Requeue,
        "/jobs/scheduled/enqueue" or "/jobs/awaiting/enqueue" => SchedulingAuditAction.Enqueue,
        "/recurring/remove" => SchedulingAuditAction.RecurringRemove,
        "/recurring/trigger" => SchedulingAuditAction.RecurringTrigger,
        _ => SchedulingAuditAction.Unknown
    };

    private static SchedulingAuditOutcome Outcome(string result) => result switch
    {
        "started" => SchedulingAuditOutcome.Started,
        "accepted" => SchedulingAuditOutcome.Accepted,
        "pending" => SchedulingAuditOutcome.Pending,
        "rejected" => SchedulingAuditOutcome.Rejected,
        _ => SchedulingAuditOutcome.Unknown
    };
}
