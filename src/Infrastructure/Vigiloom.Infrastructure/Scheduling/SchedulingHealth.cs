using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Scheduling.Persistence;

namespace Vigiloom.Infrastructure.Scheduling;

public sealed record SchedulingHeartbeats(DateTimeOffset ObservedAt, IReadOnlyList<DateTimeOffset?> Values);

/// <summary>Reads the Hangfire SQL Server heartbeat table with a cancellable, bounded query.</summary>
public sealed class SchedulingHealth(IDbContextFactory<SchedulingDbContext> contexts)
{
    public async Task<SchedulingHeartbeats> ReadAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT LastHeartbeat FROM [hangfire].[Server]";
        var values = new List<DateTimeOffset?>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                values.Add(reader.IsDBNull(0) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc)));
        }
        // Read the database clock after all rows: a blocked row may contain a heartbeat
        // newer than a clock value fetched at the beginning of the scan.
        command.CommandText = "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
        var observedAt = (DateTimeOffset?)await command.ExecuteScalarAsync(token)
            ?? throw new InvalidOperationException("Database clock unavailable.");
        return new(observedAt, values);
    }
}
