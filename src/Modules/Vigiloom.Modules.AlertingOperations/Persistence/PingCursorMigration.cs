using Microsoft.Data.SqlClient;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

internal static class PingCursorMigration
{
    internal static async Task CopyAsync(SqlConnection source, SqlConnection target, SqlTransaction transaction, CancellationToken token)
    {
        await using (var check = new SqlCommand("SELECT TOP (1) 1 FROM operations.PingTransitionCursors", target, transaction))
            if (await check.ExecuteScalarAsync(token) is not null)
                throw new InvalidOperationException("Target Ping cursors must be empty before copying.");
        await using var command = new SqlCommand("SELECT DeviceId, Target, Generation FROM network.PingTargets", source);
        await using var rows = await command.ExecuteReaderAsync(token);
        using var copy = new SqlBulkCopy(target, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = "operations.PingTransitionCursors",
            EnableStreaming = true
        };
        copy.ColumnMappings.Add("DeviceId", "DeviceId");
        copy.ColumnMappings.Add("Target", "Target");
        copy.ColumnMappings.Add("Generation", "Generation");
        await copy.WriteToServerAsync(rows, token);
    }
}
