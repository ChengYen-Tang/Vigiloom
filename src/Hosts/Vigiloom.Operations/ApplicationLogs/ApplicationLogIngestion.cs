using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Events;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.AlertingOperations.Domain.Logs;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
using Admission = Vigiloom.Infrastructure.LogAdmission.LogAdmission;

namespace Vigiloom.Operations.ApplicationLogs;

/// <summary>Stores an authorized application Log scope and charges the shared quota atomically.</summary>
public sealed class ApplicationLogIngestion(IDbContextFactory<CentralLogsDbContext> contexts, TimelineWriter timeline)
{
    public Task<LogReceipt> ReceiveAsync(ApplicationLogSubmission submission, CancellationToken token) => ReceiveBatchAsync([submission], token);

    public Task<LogReceipt> ReceiveGroupedAsync(ApplicationLogBatch batch, CancellationToken token)
    {
        if (batch?.Resources is not { Count: > 0 and <= 256 } ||
            batch.Resources.Any(resource => resource?.Scopes is null || resource.Scopes.Any(scope => scope is null)) ||
            batch.Resources.Sum(resource => (long)resource.Scopes.Count) > 256)
            return Task.FromResult(new LogReceipt(LogReceiptStatus.Invalid));
        var submissions = batch.Resources.SelectMany(resource => resource.Scopes.Select(scope =>
            new ApplicationLogSubmission(batch.SourceId, scope.ServiceId, scope.InstanceId, resource.ResourcePayload,
                scope.ScopePayload, resource.ResourceSchemaUrl, scope.ScopeSchemaUrl, scope.Items))).ToArray();
        return ReceiveBatchAsync(submissions, token);
    }

    public async Task<LogReceipt> ReceiveBatchAsync(IReadOnlyList<ApplicationLogSubmission> submissions, CancellationToken token)
    {
        if (submissions is not { Count: > 0 and <= 256 } || submissions[0] is null) return new(LogReceiptStatus.Invalid);
        var sourceId = submissions[0].SourceId;
        var count = 0;
        long bytes = 0;
        var resources = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var submission in submissions)
        {
            if (submission is null || submission.SourceId != sourceId || submission.SourceId.Version != 7 || submission.ServiceId.Version != 7 ||
                submission.InstanceId is { Version: not 7 } || submission.ResourcePayload is null or { Length: > 65536 } ||
                submission.ScopePayload is null or { Length: > 65536 } || submission.ResourceSchemaUrl is null or { Length: > 2048 } ||
                submission.ScopeSchemaUrl is null or { Length: > 2048 } || submission.Items is not { Count: > 0 and <= 10000 })
                return new(LogReceiptStatus.Invalid);
            if (resources.Add(submission.ResourcePayload)) bytes += submission.ResourcePayload.Length;
            bytes += submission.ScopePayload.Length;
            count += submission.Items.Count;
            if (count > 10000) return new(LogReceiptStatus.BatchTooLarge);
            foreach (var item in submission.Items)
            {
                if (item is null || item.OccurredAt == default || item.OccurredAt.Offset != TimeSpan.Zero ||
                    item.RecordPayload is null or { Length: > 262144 } || !ValidId(item.TraceId, 16) || !ValidId(item.SpanId, 8))
                    return new(LogReceiptStatus.Invalid);
                bytes += item.RecordPayload.Length;
                if (bytes > 4 * 1024 * 1024 + 65536) return new(LogReceiptStatus.BatchTooLarge);
            }
        }
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var received = DateTimeOffset.UtcNow;
        var logs = new List<LogRecord>(count);
        foreach (var submission in submissions)
        {
            var context = new LogContext
            {
                ServiceId = submission.ServiceId, ResourcePayload = submission.ResourcePayload, ScopePayload = submission.ScopePayload,
                ResourceSchemaUrl = submission.ResourceSchemaUrl, ScopeSchemaUrl = submission.ScopeSchemaUrl
            };
            foreach (var item in submission.Items)
            {
                var log = new LogRecord
                {
                    ServiceId = submission.ServiceId, InstanceId = submission.InstanceId, Context = context,
                    OccurredAt = item.OccurredAt, ReceivedAt = received, UsedReceivedTime = item.UsedReceivedTime,
                    UsedObservedTime = item.UsedObservedTime, TraceId = item.TraceId, SpanId = item.SpanId, RecordPayload = item.RecordPayload
                };
                logs.Add(log);
                db.LogRecords.Add(log);
            }
        }
        await db.SaveChangesAsync(token);
        var admission = await Admission.AdmitAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new(LogSourceKind.Application, sourceId), count, token);
        if (admission.Status != LogAdmissionStatus.Accepted)
            return new(admission.Status switch
            {
                LogAdmissionStatus.RateLimited => LogReceiptStatus.RateLimited,
                LogAdmissionStatus.BatchTooLarge => LogReceiptStatus.BatchTooLarge,
                _ => LogReceiptStatus.Busy
            }, admission.RetryAfter);
        var entries = logs.Select(item => (new EventTimelineEntry("service-log", item.OccurredAt, item.ServiceId, "service",
            "應用程式日誌", item.TraceId is { Length: > 0 } ? $"Trace {Convert.ToHexStringLower(item.TraceId)}" :
                $"服務實例 {item.InstanceId?.ToString("D") ?? "未指定"}", $"/services/{item.ServiceId:D}"), item.RowId));
        await timeline.AppendLocalAsync(entries, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
        await transaction.CommitAsync(token);
        return new(LogReceiptStatus.Accepted);
    }

    private static bool ValidId(byte[]? value, int length) => value is null || value.Length == length && value.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
}
