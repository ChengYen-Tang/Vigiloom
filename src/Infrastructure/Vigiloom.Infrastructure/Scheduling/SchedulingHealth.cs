using Microsoft.Data.SqlClient;

namespace Vigiloom.Infrastructure.Scheduling;

public sealed record SchedulingHeartbeats(DateTimeOffset ObservedAt, IReadOnlyList<DateTimeOffset?> Values);

/// <summary>Reads the Hangfire SQL Server heartbeat table with a cancellable, bounded query.</summary>
public sealed class SchedulingHealth(SchedulingReadStorage storage)
{
    public async Task<SchedulingHeartbeats> ReadAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(storage.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
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
