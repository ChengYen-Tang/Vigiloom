using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Worker.Hosting;

/// <summary>Removes expired failed Hangfire jobs without touching jobs awaiting retry.</summary>
public sealed class FailedJobRetention(
    SchedulingReadStorage storage,
    IDbContextFactory<OperationsDbContext> contexts,
    AuditRetentionSettings settings)
{
    private const int BatchSize = 100;

    public async Task<int> RunAsync(CancellationToken token)
    {
        await settings.GetAsync(token); // Ensure the retention setting exists.
        var monitor = storage.Storage.GetMonitoringApi();
        var offset = 0;
        var total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var batch = monitor.FailedJobs(offset, BatchSize);
            if (batch.Count == 0) return total;
            var removed = 0;
            await using (var db = await contexts.CreateDbContextAsync(token))
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var policy = await db.Set<AuditRetentionSetting>().FromSqlRaw(
                    "SELECT * FROM [operations].[AuditRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
                var cutoff = DateTime.UtcNow.AddDays(-policy.Days);
                foreach (var job in batch)
                {
                    token.ThrowIfCancellationRequested();
                    if (job.Value.FailedAt is { } failedAt && failedAt < cutoff && DeleteIfExpired(job.Key, cutoff, token)) removed++;
                }
                await transaction.CommitAsync(token);
            }
            total += removed;
            offset += batch.Count - removed;
            if (batch.Count < BatchSize) return total;
        }
    }

    private bool DeleteIfExpired(string jobId, DateTime cutoff, CancellationToken token)
    {
        using var connection = storage.Storage.GetConnection();
        using var transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
        transaction.AcquireDistributedJobLock(jobId, TimeSpan.FromSeconds(5));
        var state = connection.GetStateData(jobId);
        if (state?.Name != FailedState.StateName || !state.Data.TryGetValue("FailedAt", out var failedAt) ||
            JobHelper.DeserializeDateTime(failedAt) >= cutoff) return false;
        var change = new StateChangeContext(storage.Storage, connection, transaction, jobId,
            new DeletedState(), [FailedState.StateName], token);
        var result = new BackgroundJobStateChanger().ChangeState(change);
        transaction.Commit();
        return result?.Name == DeletedState.StateName;
    }
}
