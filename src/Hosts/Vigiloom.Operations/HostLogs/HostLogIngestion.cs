using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Domain.Logs;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Admission = Vigiloom.Infrastructure.LogAdmission.LogAdmission;

namespace Vigiloom.Operations.HostLogs;

/// <summary>Stores authenticated host Logs, shared quota usage and security work atomically.</summary>
public sealed class HostLogIngestion(IDbContextFactory<CentralLogsDbContext> contexts, TemporalWorkWriter work, IConfiguration configuration)
{
    public async Task<HostLogReceipt> ReceiveAsync(HostLogSubmission submission, CancellationToken token)
    {
        if (submission.AgentId.Version != 7 || submission.DeviceId.Version != 7 || submission.DeviceGeneration < 0 || submission.Batch is null)
            return new(HostLogReceiptStatus.Invalid);
        try { _ = AgentLogBatchCodec.Serialize(submission.Batch); }
        catch (ArgumentException) { return new(HostLogReceiptStatus.Invalid); }
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
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) return new(HostLogReceiptStatus.Busy);
        }
        var ids = batch.Items.Select(item => item.MessageId).ToArray();
        var existing = await db.HostLogs.AsNoTracking().Where(log => log.AgentId == submission.AgentId && ids.Contains(log.MessageId))
            .ToDictionaryAsync(log => log.MessageId, token);
        foreach (var item in batch.Items)
            if (existing.TryGetValue(item.MessageId, out var previous) && !previous.Matches(batch, item))
                return new(HostLogReceiptStatus.Conflict);
        var newIds = new List<Guid>(batch.Items.Count - existing.Count);
        var receivedAt = DateTimeOffset.UtcNow;
        for (var i = 0; i < batch.Items.Count; i++)
        {
            if (existing.ContainsKey(batch.Items[i].MessageId)) continue;
            newIds.Add(batch.Items[i].MessageId);
            db.HostLogs.Add(new HostLog(submission.AgentId, batch, i, receivedAt));
        }
        if (newIds.Count == 0) return new(HostLogReceiptStatus.Accepted);
        await db.SaveChangesAsync(token);
        var admission = await Admission.AdmitAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new(LogSourceKind.Agent, submission.AgentId), newIds.Count, token);
        if (admission.Status != LogAdmissionStatus.Accepted)
            return new(admission.Status switch
            {
                LogAdmissionStatus.RateLimited => HostLogReceiptStatus.RateLimited,
                LogAdmissionStatus.BatchTooLarge => HostLogReceiptStatus.BatchTooLarge,
                _ => HostLogReceiptStatus.Busy
            }, admission.RetryAfter);
        var dispatch = new HostLogDispatch(submission.AgentId, submission.DeviceId, submission.DeviceGeneration, newIds);
        await work.WriteAsync(db, configuration["Work:SecurityLogQueue"] ?? "vigiloom.security.logs",
            new ServiceWork(HostLogDispatch.MessageType, JsonSerializer.Serialize(dispatch)), token);
        await transaction.CommitAsync(token);
        return new(HostLogReceiptStatus.Accepted);
    }
}
