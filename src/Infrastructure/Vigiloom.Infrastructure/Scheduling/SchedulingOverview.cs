using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling.Persistence;

namespace Vigiloom.Infrastructure.Scheduling;

public sealed record WorkEntry(string Name, string Result, DateTimeOffset? OccurredAt, string? Detail, string? Reference = null);
public sealed record WorkOverview(long Workers, long Queued, long Processing, long Failed, long Succeeded, int PendingMessages, int FailedMessages, IReadOnlyList<WorkEntry> Recent);

public sealed class SchedulingOverview(SchedulingReadStorage storage, IDbContextFactory<SchedulingDbContext> contexts)
{
    public async Task<WorkOverview> GetAsync(CancellationToken cancellationToken = default)
    {
        var monitor = storage.Storage.GetMonitoringApi();
        cancellationToken.ThrowIfCancellationRequested();
        var stats = monitor.GetStatistics();
        var recent = new List<WorkEntry>();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in monitor.SucceededJobs(0, 10))
            recent.Add(new WorkEntry(JobName(entry.Value.Job?.Type), "已完成", AsUtc(entry.Value.SucceededAt), null));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in monitor.FailedJobs(0, 10))
            recent.Add(new WorkEntry(JobName(entry.Value.Job?.Type), "失敗", AsUtc(entry.Value.FailedAt), JobFailure(entry.Value.ExceptionType), entry.Key));

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Database.SetCommandTimeout(TimeSpan.FromSeconds(5));
        var pendingMessages = await db.Outbox.AsNoTracking().CountAsync(record => record.State == 0 || record.State == 1, cancellationToken);
        var failedMessages = await db.Outbox.AsNoTracking().CountAsync(record => record.State == 3, cancellationToken);
        var failures = await db.Outbox.AsNoTracking().Where(record => record.LastFailureCode != null && record.State != 2)
            .OrderByDescending(record => record.LastAttemptAt).Take(10)
            .Select(record => new { record.MessageId, record.State, record.LastAttemptAt, record.LastFailureCode, record.Attempts })
            .ToListAsync(cancellationToken);
        recent.AddRange(failures.Select(record => new WorkEntry("背景資料處理", record.State == 3 ? "已停止重試" : "等待重試",
            record.LastAttemptAt, $"{Failure(record.LastFailureCode)}（嘗試 {record.Attempts} 次）", record.MessageId.ToString("D"))));
        return new WorkOverview(stats.Servers, stats.Enqueued, stats.Processing, stats.Failed, stats.Succeeded, pendingMessages, failedMessages,
            recent.OrderByDescending(entry => entry.OccurredAt).Take(20).ToArray());
    }

    private static string JobName(Type? type) => type == typeof(Outbox.OutboxDispatcher) ? "背景資料處理" : "背景工作";
    private static DateTimeOffset? AsUtc(DateTime? value) => value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    private static string JobFailure(string? type) => type switch
    {
        "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException" => "資料庫連線或操作失敗。",
        "System.TimeoutException" => "工作執行逾時。",
        "System.OperationCanceledException" or "System.Threading.Tasks.TaskCanceledException" => "工作已取消。",
        _ => "工作執行失敗；請查看進階管理。"
    };
    private static string Failure(string? code) => code switch
    {
        "unknown_type" => "無法辨識工作類型",
        "attempts_exhausted" => "已達重試上限",
        "handler_timeout" => "背景資料處理逾時",
        _ => "背景資料處理失敗"
    };
}
