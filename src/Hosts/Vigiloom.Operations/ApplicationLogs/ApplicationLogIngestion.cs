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
    public async Task<LogReceipt> ReceiveAsync(ApplicationLogSubmission submission, CancellationToken token)
    {
        if (submission.SourceId.Version != 7 || submission.ServiceId.Version != 7 ||
            submission.InstanceId is { Version: not 7 } || submission.ResourcePayload is null or { Length: > 65536 } ||
            submission.ScopePayload is null or { Length: > 65536 } || submission.ResourceSchemaUrl is null or { Length: > 2048 } ||
            submission.ScopeSchemaUrl is null or { Length: > 2048 } || submission.Items is not { Count: > 0 and <= 10000 })
            return new(LogReceiptStatus.Invalid);
        long bytes = submission.ResourcePayload.Length + submission.ScopePayload.Length;
        foreach (var item in submission.Items)
        {
            if (item is null || item.OccurredAt == default || item.OccurredAt.Offset != TimeSpan.Zero ||
                item.RecordPayload is null or { Length: > 262144 } || !ValidId(item.TraceId, 16) || !ValidId(item.SpanId, 8))
                return new(LogReceiptStatus.Invalid);
            bytes += item.RecordPayload.Length;
            if (bytes > 4 * 1024 * 1024 + 65536) return new(LogReceiptStatus.BatchTooLarge);
        }
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var context = new LogContext
        {
            ServiceId = submission.ServiceId, ResourcePayload = submission.ResourcePayload, ScopePayload = submission.ScopePayload,
            ResourceSchemaUrl = submission.ResourceSchemaUrl, ScopeSchemaUrl = submission.ScopeSchemaUrl
        };
        var received = DateTimeOffset.UtcNow;
        foreach (var item in submission.Items)
            db.LogRecords.Add(new LogRecord
            {
                ServiceId = submission.ServiceId, InstanceId = submission.InstanceId, Context = context,
                OccurredAt = item.OccurredAt, ReceivedAt = received, UsedReceivedTime = item.UsedReceivedTime,
                UsedObservedTime = item.UsedObservedTime, TraceId = item.TraceId, SpanId = item.SpanId, RecordPayload = item.RecordPayload
            });
        await db.SaveChangesAsync(token);
        var admission = await Admission.AdmitAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new(LogSourceKind.Application, submission.SourceId), submission.Items.Count, token);
        if (admission.Status != LogAdmissionStatus.Accepted)
            return new(admission.Status switch
            {
                LogAdmissionStatus.RateLimited => LogReceiptStatus.RateLimited,
                LogAdmissionStatus.BatchTooLarge => LogReceiptStatus.BatchTooLarge,
                _ => LogReceiptStatus.Busy
            }, admission.RetryAfter);
        var entries = submission.Items.Select(item => new EventTimelineEntry("service-log", item.OccurredAt, submission.ServiceId, "service",
            "應用程式日誌", item.TraceId is { Length: > 0 } ? $"Trace {Convert.ToHexStringLower(item.TraceId)}" :
                $"服務實例 {submission.InstanceId?.ToString("D") ?? "未指定"}", $"/services/{submission.ServiceId:D}"));
        await timeline.AppendLocalAsync(entries, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
        await transaction.CommitAsync(token);
        return new(LogReceiptStatus.Accepted);
    }

    private static bool ValidId(byte[]? value, int length) => value is null || value.Length == length && value.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
}
