using System.Data;
using Microsoft.Data.SqlClient;

namespace Vigiloom.Infrastructure.Persistence;

/// <summary>Copies known EF-managed tables into an empty migrated database in one transaction.</summary>
public static class SqlTableDataMigration
{
    public static void ValidateTarget(string connectionString)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(target.InitialCatalog) || IsSystemDatabase(target.InitialCatalog))
            throw new InvalidOperationException("The target must be a non-system database.");
    }

    public static void ValidateConnections(string sourceConnection, string targetConnection)
    {
        var source = new SqlConnectionStringBuilder(sourceConnection);
        var target = new SqlConnectionStringBuilder(targetConnection);
        ValidateTarget(targetConnection);
        if (string.IsNullOrWhiteSpace(source.InitialCatalog) || IsSystemDatabase(source.InitialCatalog) ||
            (string.Equals(source.DataSource, target.DataSource, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(source.InitialCatalog, target.InitialCatalog, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Source and target must be distinct non-system databases.");
    }

    public sealed record TableMapping(string SourceSchema, string SourceTable, string TargetSchema, string TargetTable,
        string? FilterColumn = null, string? FilterValue = null, IReadOnlyList<string>? OptionalSourceColumns = null);

    public static Task CopyAsync(string sourceConnection, string targetConnection, string schema, IReadOnlyList<string> tables, CancellationToken token = default) =>
        CopyAsync(sourceConnection, targetConnection, tables.Select(table => new TableMapping(schema, table, schema, table)).ToArray(), token);

    public static async Task CopyAsync(string sourceConnection, string targetConnection, IReadOnlyList<TableMapping> tables, CancellationToken token = default,
        Func<SqlConnection, SqlConnection, SqlTransaction, CancellationToken, Task>? afterCopy = null)
    {
        ValidateConnections(sourceConnection, targetConnection);
        await using var source = new SqlConnection(sourceConnection);
        await using var target = new SqlConnection(targetConnection);
        await source.OpenAsync(token);
        await target.OpenAsync(token);
        await using var transaction = (SqlTransaction)await target.BeginTransactionAsync(token);
        using var quote = new SqlCommandBuilder();
        foreach (var table in tables)
        {
            var tableName = quote.QuoteIdentifier(table.TargetSchema) + "." + quote.QuoteIdentifier(table.TargetTable);
            await using var empty = new SqlCommand($"SELECT TOP (1) 1 FROM {tableName}", target, transaction);
            if (await empty.ExecuteScalarAsync(token) is not null)
                throw new InvalidOperationException($"Target table {table.TargetSchema}.{table.TargetTable} must be empty before copying.");
        }
        foreach (var table in tables)
        {
            await using var metadata = new SqlCommand("""
                SELECT c.name FROM sys.columns c
                INNER JOIN sys.tables t ON t.object_id = c.object_id
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = @schema AND t.name = @table AND c.is_computed = 0 AND c.system_type_id <> 189
                ORDER BY c.column_id
                """, target, transaction);
            metadata.Parameters.AddWithValue("@schema", table.TargetSchema);
            metadata.Parameters.AddWithValue("@table", table.TargetTable);
            var columns = new List<string>();
            await using (var reader = await metadata.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
            if (columns.Count == 0) throw new InvalidOperationException($"Target table {table.TargetSchema}.{table.TargetTable} is missing.");
            if (table.OptionalSourceColumns is { Count: > 0 } optional)
            {
                await using var sourceMetadata = new SqlCommand("""
                    SELECT c.name FROM sys.columns c
                    INNER JOIN sys.tables t ON t.object_id = c.object_id
                    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                    WHERE s.name = @schema AND t.name = @table
                    """, source);
                sourceMetadata.Parameters.AddWithValue("@schema", table.SourceSchema);
                sourceMetadata.Parameters.AddWithValue("@table", table.SourceTable);
                var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await sourceMetadata.ExecuteReaderAsync(token))
                    while (await reader.ReadAsync(token)) available.Add(reader.GetString(0));
                columns.RemoveAll(column => optional.Contains(column, StringComparer.OrdinalIgnoreCase) && !available.Contains(column));
            }
            var sourceTable = quote.QuoteIdentifier(table.SourceSchema) + "." + quote.QuoteIdentifier(table.SourceTable);
            var targetTable = quote.QuoteIdentifier(table.TargetSchema) + "." + quote.QuoteIdentifier(table.TargetTable);
            var projection = string.Join(", ", columns.Select(quote.QuoteIdentifier));
            var filter = table.FilterColumn is null ? "" : $" WHERE {quote.QuoteIdentifier(table.FilterColumn)} = @filter";
            await using var command = new SqlCommand($"SELECT {projection} FROM {sourceTable}{filter}", source);
            if (table.FilterColumn is not null) command.Parameters.AddWithValue("@filter", table.FilterValue ?? throw new InvalidOperationException("A filter value is required."));
            await using var rows = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            using var copy = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints, transaction)
            {
                DestinationTableName = targetTable,
                EnableStreaming = true
            };
            foreach (var column in columns) copy.ColumnMappings.Add(column, column);
            await copy.WriteToServerAsync(rows, token);
        }
        if (afterCopy is not null) await afterCopy(source, target, transaction, token);
        await transaction.CommitAsync(token);
    }

    private static bool IsSystemDatabase(string name) =>
        new[] { "master", "model", "msdb", "tempdb" }.Contains(name, StringComparer.OrdinalIgnoreCase);
}
