using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Events;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Domain.Logs;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
using Admission = Vigiloom.Infrastructure.LogAdmission.LogAdmission;

namespace Vigiloom.Operations.HostLogs;

/// <summary>Stores authenticated host Logs, shared quota usage and security work atomically.</summary>
public sealed class HostLogIngestion(IDbContextFactory<CentralLogsDbContext> contexts, TemporalWorkWriter work, IConfiguration configuration, TimelineWriter timeline)
{
    public async Task<LogReceipt> ReceiveAsync(HostLogSubmission submission, CancellationToken token)
    {
        if (submission.AgentId.Version != 7 || submission.DeviceId.Version != 7 || submission.DeviceGeneration < 0 || submission.Batch is null)
            return new(LogReceiptStatus.Invalid);
        try { _ = AgentLogBatchCodec.Serialize(submission.Batch); }
        catch (ArgumentException) { return new(LogReceiptStatus.Invalid); }
        var batch = submission.Batch;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @result;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.Value = $"Vigiloom.HostLogs.{submission.AgentId:D}";
            command.Parameters.Add(parameter);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) return new(LogReceiptStatus.Busy);
        }
        var ids = batch.Items.Select(item => item.MessageId).ToArray();
        var existing = await db.HostLogs.AsNoTracking().Where(log => log.AgentId == submission.AgentId && ids.Contains(log.MessageId))
            .ToDictionaryAsync(log => log.MessageId, token);
        foreach (var item in batch.Items)
            if (existing.TryGetValue(item.MessageId, out var previous) && !previous.Matches(batch, item))
                return new(LogReceiptStatus.Conflict);
        var newIds = new List<Guid>(batch.Items.Count - existing.Count);
        var entries = new List<(EventTimelineEntry Entry, HostLog Log)>(batch.Items.Count - existing.Count);
        var receivedAt = DateTimeOffset.UtcNow;
        for (var i = 0; i < batch.Items.Count; i++)
        {
            if (existing.ContainsKey(batch.Items[i].MessageId)) continue;
            newIds.Add(batch.Items[i].MessageId);
            var log = new HostLog(submission.AgentId, batch, i, receivedAt);
            db.HostLogs.Add(log);
            var item = batch.Items[i];
            entries.Add((new("host-log", receivedAt, submission.DeviceId, "device",
                item.Gap is null ? $"主機日誌：{batch.SourceName}" : $"主機日誌採集缺口：{batch.SourceName}",
                $"解析版本 {batch.ParserVersion}；{submission.AgentId:D}/{item.MessageId:D}；原文：{(item.Content.Length > 512 ? item.Content[..512] + "…" : item.Content)}",
                $"/devices/{submission.DeviceId:D}/host-logs/{submission.AgentId:D}/{item.MessageId:D}"), log));
        }
        if (newIds.Count == 0) return new(LogReceiptStatus.Accepted);
        await db.SaveChangesAsync(token);
        var admission = await Admission.AdmitAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new(LogSourceKind.Agent, submission.AgentId), newIds.Count, token);
        if (admission.Status != LogAdmissionStatus.Accepted)
            return new(admission.Status switch
            {
                LogAdmissionStatus.RateLimited => LogReceiptStatus.RateLimited,
                LogAdmissionStatus.BatchTooLarge => LogReceiptStatus.BatchTooLarge,
                _ => LogReceiptStatus.Busy
            }, admission.RetryAfter);
        await timeline.AppendLocalAsync(entries.Select(item => (item.Entry, item.Log.RowId)), db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
        var dispatch = new HostLogDispatch(submission.AgentId, submission.DeviceId, submission.DeviceGeneration, newIds);
        await work.WriteAsync(db, configuration["Work:SecurityLogQueue"] ?? "vigiloom.security.logs",
            new ServiceWork(HostLogDispatch.CombinedMessageType, JsonSerializer.Serialize(dispatch)), token);
        await transaction.CommitAsync(token);
        return new(LogReceiptStatus.Accepted);
    }
}
