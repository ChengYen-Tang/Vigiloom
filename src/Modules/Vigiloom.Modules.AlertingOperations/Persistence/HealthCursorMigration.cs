using Microsoft.Data.SqlClient;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

internal static class HealthCursorMigration
{
    internal static async Task CopyAsync(SqlConnection source, SqlConnection target, SqlTransaction transaction, CancellationToken token)
    {
        await using (var check = new SqlCommand("SELECT TOP (1) 1 FROM operations.HealthTransitionCursors", target, transaction))
            if (await check.ExecuteScalarAsync(token) is not null)
                throw new InvalidOperationException("Target health cursors must be empty before copying.");
        await using var command = new SqlCommand("SELECT TargetId, Generation FROM service.HealthSchedules", source);
        await using var rows = await command.ExecuteReaderAsync(token);
        using var copy = new SqlBulkCopy(target, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = "operations.HealthTransitionCursors",
            EnableStreaming = true
        };
        copy.ColumnMappings.Add("TargetId", "TargetId");
        copy.ColumnMappings.Add("Generation", "Generation");
        await copy.WriteToServerAsync(rows, token);
    }
}
