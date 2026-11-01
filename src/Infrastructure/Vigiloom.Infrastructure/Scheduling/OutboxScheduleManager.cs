using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Infrastructure.Scheduling;

public sealed record ScheduleSnapshot(int Minutes, string Version, bool Applied);
public sealed class ScheduleConflictException : Exception;
public sealed class ScheduleApplyPendingException(Exception inner) : Exception("The saved schedule is awaiting application.", inner);

/// <summary>Persists desired settings with rowversion and reconciles the Hangfire schedule across all hosts.</summary>
public sealed class OutboxScheduleManager(IServiceProvider services, IDbContextFactory<SchedulingDbContext> contexts)
{
    public const string ScheduleId = "vigiloom-outbox-dispatch";
    private const string LockId = "vigiloom:outbox-schedule";
    private static readonly int[] Intervals = [1, 2, 5, 10, 15, 30, 60];
    public static bool IsValidInterval(int minutes) => Intervals.Contains(minutes);

    public ScheduleSnapshot GetSnapshot()
    {
        using var db = contexts.CreateDbContext();
        var setting = db.ScheduleSettings.AsNoTracking().SingleOrDefault(record => record.Name == ScheduleId);
        var minutes = setting?.IntervalMinutes ?? 1;
        using var connection = services.GetRequiredService<JobStorage>().GetConnection();
        var data = connection.GetAllEntriesFromHash($"recurring-job:{ScheduleId}");
        var applied = setting is not null && data is not null && data.TryGetValue("Cron", out var cron) && cron == CronFor(minutes);
        return new ScheduleSnapshot(minutes, setting is null ? "" : Convert.ToBase64String(setting.Version), applied);
    }

    public void EnsureDefault()
    {
        using var connection = services.GetRequiredService<JobStorage>().GetConnection();
        using var gate = connection.AcquireDistributedLock(LockId, TimeSpan.FromSeconds(15));
        using var db = contexts.CreateDbContext();
        var setting = db.ScheduleSettings.SingleOrDefault(record => record.Name == ScheduleId);
        if (setting is null)
        {
            var now = DateTimeOffset.UtcNow;
            setting = new ScheduleSetting { Id = ResourceIds.Create(), Name = ScheduleId, IntervalMinutes = 1, CreatedAt = now, UpdatedAt = now };
            db.ScheduleSettings.Add(setting);
            db.SaveChanges();
        }
        var data = connection.GetAllEntriesFromHash($"recurring-job:{ScheduleId}");
        if (data is null || !data.TryGetValue("Cron", out var cron) || cron != CronFor(setting.IntervalMinutes)) Update(setting.IntervalMinutes);
    }

    public void SetIntervalMinutes(int minutes, string expectedVersion, Guid actor)
    {
        if (!IsValidInterval(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (actor == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actor));
        using var connection = services.GetRequiredService<JobStorage>().GetConnection();
        using var gate = connection.AcquireDistributedLock(LockId, TimeSpan.FromSeconds(15));
        using var db = contexts.CreateDbContext();
        var setting = db.ScheduleSettings.SingleOrDefault(record => record.Name == ScheduleId);
        if (!string.Equals(setting is null ? "" : Convert.ToBase64String(setting.Version), expectedVersion, StringComparison.Ordinal))
            throw new ScheduleConflictException();
        if (setting is null)
        {
            setting = new ScheduleSetting { Id = ResourceIds.Create(), Name = ScheduleId, CreatedAt = DateTimeOffset.UtcNow };
            db.ScheduleSettings.Add(setting);
        }
        setting.IntervalMinutes = minutes;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        setting.UpdatedBy = actor;
        db.SaveChanges();
        try { Update(minutes); }
        catch (Exception exception) { throw new ScheduleApplyPendingException(exception); }
    }

    private void Update(int minutes) => services.GetRequiredService<IRecurringJobManager>().AddOrUpdate(ScheduleId,
        Job.FromExpression<OutboxDispatcher>(dispatcher => dispatcher.DispatchAsync(CancellationToken.None)), CronFor(minutes),
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    private static string CronFor(int minutes) => minutes == 60 ? "0 * * * *" : $"*/{minutes} * * * *";
}
