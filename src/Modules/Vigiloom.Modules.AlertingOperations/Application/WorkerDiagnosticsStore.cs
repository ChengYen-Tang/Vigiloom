using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Bounded, per-boot scanner observations; this is not an event history.</summary>
public enum ScannerFailureKind : short { None, Database, Timeout, Other }
public readonly record struct ScannerObservation(DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, int ConsecutiveFailures, ScannerFailureKind FailureKind = ScannerFailureKind.None);
public sealed record WorkerObservation(ScannerObservation Ping, ScannerObservation Speed);
public sealed record WorkerDiagnostic(Guid BootId, DateTimeOffset? ObservedAt, DateTimeOffset? LeaseUntil, WorkerObservation Observation);
public sealed record WorkerDiagnosticsSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<WorkerDiagnostic> Workers);

public sealed class WorkerDiagnosticsStore(IDbContextFactory<OperationsDbContext> contexts)
{
    private const int LeaseSeconds = 120;

    /// <summary>Claim a free or expired slot. A new boot ID must be used for every claim.</summary>
    public async Task<int?> ClaimAsync(Guid bootId, CancellationToken token)
    {
        if (bootId == Guid.Empty) throw new ArgumentException("A boot identifier is required.", nameof(bootId));
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            ;WITH candidate AS
            (
                SELECT TOP (1) * FROM operations.WorkerDiagnosticSlots WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE LeaseUntil IS NULL OR LeaseUntil <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
                ORDER BY Slot
            )
            UPDATE candidate SET BootId = @boot,
                ObservedAt = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                LeaseUntil = DATEADD(second, @lease, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
                PingSuccessAt = NULL, PingFailureAt = NULL, PingFailures = 0, PingFailureKind = 0,
                SpeedSuccessAt = NULL, SpeedFailureAt = NULL, SpeedFailures = 0, SpeedFailureKind = 0
            OUTPUT inserted.Slot;
            """;
        command.Parameters.Add(new SqlParameter("@boot", SqlDbType.UniqueIdentifier) { Value = bootId });
        command.Parameters.Add(new SqlParameter("@lease", SqlDbType.Int) { Value = LeaseSeconds });
        var slot = await command.ExecuteScalarAsync(token);
        return slot is null ? null : Convert.ToInt32(slot);
    }

    /// <summary>Renew and update an unexpired slot only; an old owner can never revive it.</summary>
    public async Task<bool> UpdateAsync(int slot, Guid bootId, WorkerObservation observation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (slot is < 1 or > 64 || bootId == Guid.Empty || observation.Ping.ConsecutiveFailures < 0 || observation.Speed.ConsecutiveFailures < 0 ||
            !Enum.IsDefined(observation.Ping.FailureKind) || !Enum.IsDefined(observation.Speed.FailureKind))
            throw new ArgumentException("Invalid worker diagnostic observation.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            UPDATE operations.WorkerDiagnosticSlots
            SET ObservedAt = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                LeaseUntil = DATEADD(second, @lease, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
                PingSuccessAt = @pingSuccess, PingFailureAt = @pingFailure, PingFailures = @pingFailures, PingFailureKind = @pingFailureKind,
                SpeedSuccessAt = @speedSuccess, SpeedFailureAt = @speedFailure, SpeedFailures = @speedFailures, SpeedFailureKind = @speedFailureKind
            WHERE Slot = @slot AND BootId = @boot
              AND LeaseUntil > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
            """;
        command.Parameters.Add(new SqlParameter("@slot", SqlDbType.Int) { Value = slot });
        command.Parameters.Add(new SqlParameter("@boot", SqlDbType.UniqueIdentifier) { Value = bootId });
        command.Parameters.Add(new SqlParameter("@lease", SqlDbType.Int) { Value = LeaseSeconds });
        AddScanner(command, "ping", observation.Ping);
        AddScanner(command, "speed", observation.Speed);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<IReadOnlyList<WorkerDiagnostic>> GetAsync(CancellationToken token)
        => (await GetSnapshotAsync(token)).Workers;

    public async Task<WorkerDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var workers = await db.WorkerDiagnosticSlots.AsNoTracking().Where(x => x.BootId != null)
            .OrderBy(x => x.Slot)
            .Select(x => new WorkerDiagnostic(x.BootId!.Value, x.ObservedAt, x.LeaseUntil,
                new WorkerObservation(new ScannerObservation(x.PingSuccessAt, x.PingFailureAt, x.PingFailures, (ScannerFailureKind)x.PingFailureKind),
                    new ScannerObservation(x.SpeedSuccessAt, x.SpeedFailureAt, x.SpeedFailures, (ScannerFailureKind)x.SpeedFailureKind))))
            .ToListAsync(token);
        var observedAt = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]")
            .SingleAsync(token);
        return new(observedAt, workers);
    }

    private static void AddScanner(System.Data.Common.DbCommand command, string prefix, ScannerObservation observation)
    {
        command.Parameters.Add(new SqlParameter($"@{prefix}Success", SqlDbType.DateTimeOffset) { Value = (object?)observation.LastSuccess?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter($"@{prefix}Failure", SqlDbType.DateTimeOffset) { Value = (object?)observation.LastFailure?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter($"@{prefix}Failures", SqlDbType.Int) { Value = observation.ConsecutiveFailures });
        command.Parameters.Add(new SqlParameter($"@{prefix}FailureKind", SqlDbType.SmallInt) { Value = (short)observation.FailureKind });
    }
}
