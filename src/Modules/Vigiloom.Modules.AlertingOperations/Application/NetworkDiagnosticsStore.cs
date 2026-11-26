using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Stores only live Network scanner snapshots.</summary>
public sealed class NetworkDiagnosticsStore(IDbContextFactory<OperationsDbContext> contexts)
{
    public async Task<int> CleanupExpiredAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Database.ExecuteSqlRawAsync("DELETE FROM [operations].[NetworkDiagnostics] WHERE [LeaseUntil] <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')", token);
    }
    public async Task SaveAsync(NetworkDiagnosticUpdate input, CancellationToken token)
    {
        if (input.BootId.Version != 7 || input.Ping is null || input.Speed is null ||
            input.Ping.ConsecutiveFailures < 0 || input.Speed.ConsecutiveFailures < 0 ||
            !Enum.IsDefined(input.Ping.FailureKind) || !Enum.IsDefined(input.Speed.FailureKind))
            throw new ArgumentException("Invalid scanner observation.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var row = await db.NetworkDiagnostics.FromSqlInterpolated($"SELECT * FROM [operations].[NetworkDiagnostics] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {input.BootId}").SingleOrDefaultAsync(token);
        if (row is null) { row = new NetworkDiagnostic { Id = input.BootId }; db.NetworkDiagnostics.Add(row); }
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        row.ObservedAt = now; row.LeaseUntil = now.AddSeconds(120);
        row.PingSuccessAt = input.Ping.LastSuccess; row.PingFailureAt = input.Ping.LastFailure;
        row.PingFailures = input.Ping.ConsecutiveFailures; row.PingFailureKind = (short)input.Ping.FailureKind;
        row.SpeedSuccessAt = input.Speed.LastSuccess; row.SpeedFailureAt = input.Speed.LastFailure;
        row.SpeedFailures = input.Speed.ConsecutiveFailures; row.SpeedFailureKind = (short)input.Speed.FailureKind;
        await db.SaveChangesAsync(token);
        await db.NetworkDiagnostics.Where(x => x.LeaseUntil < now).ExecuteDeleteAsync(token);
        await tx.CommitAsync(token);
    }

    public async Task<WorkerDiagnosticsInfo> ReadAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var rows = await db.NetworkDiagnostics.AsNoTracking().Where(x => x.LeaseUntil > now).ToArrayAsync(token);
        return new(now, rows.Select(x => new WorkerDiagnosticInfo(x.Id, x.ObservedAt, x.LeaseUntil,
            new(x.PingSuccessAt, x.PingFailureAt, x.PingFailures, (ScannerDiagnosticFailure)x.PingFailureKind),
            new(x.SpeedSuccessAt, x.SpeedFailureAt, x.SpeedFailures, (ScannerDiagnosticFailure)x.SpeedFailureKind))).ToArray());
    }
}
