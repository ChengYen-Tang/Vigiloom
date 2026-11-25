using System.Data;
using Microsoft.Data.SqlClient;

namespace Vigiloom.Modules.IdentityAccess.Persistence;

/// <summary>Copies existing Identity data into an empty, EF-migrated Identity database.</summary>
public static class IdentityDataMigration
{
    private static readonly string[] Tables =
    [
        "AspNetRoles", "AspNetUsers", "AspNetRoleClaims", "AspNetUserClaims", "AspNetUserLogins",
        "AspNetUserRoles", "AspNetUserTokens", "AccountAuditEvents", "ApiCredentials", "ApiCredentialAuditEvents"
    ];

    public static void ValidateConnections(string sourceConnection, string targetConnection)
    {
        var source = new SqlConnectionStringBuilder(sourceConnection);
        var target = new SqlConnectionStringBuilder(targetConnection);
        var systemDatabases = new[] { "master", "model", "msdb", "tempdb" };
        ValidateTarget(targetConnection);
        if (string.IsNullOrWhiteSpace(source.InitialCatalog) ||
            systemDatabases.Contains(source.InitialCatalog, StringComparer.OrdinalIgnoreCase) ||
            (string.Equals(source.DataSource, target.DataSource, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(source.InitialCatalog, target.InitialCatalog, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Source and target must be distinct non-system databases.");
    }

    public static void ValidateTarget(string targetConnection)
    {
        var target = new SqlConnectionStringBuilder(targetConnection);
        if (string.IsNullOrWhiteSpace(target.InitialCatalog) ||
            new[] { "master", "model", "msdb", "tempdb" }.Contains(target.InitialCatalog, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Identity target must be a non-system database.");
    }

    public static async Task CopyAsync(string sourceConnection, string targetConnection, CancellationToken token = default)
    {
        ValidateConnections(sourceConnection, targetConnection);
        await using var sourceDb = new SqlConnection(sourceConnection);
        await using var targetDb = new SqlConnection(targetConnection);
        await sourceDb.OpenAsync(token);
        await targetDb.OpenAsync(token);
        await using var transaction = (SqlTransaction)await targetDb.BeginTransactionAsync(token);
        foreach (var table in Tables)
        {
            await using var empty = new SqlCommand($"SELECT TOP (1) 1 FROM [identity].[{table}]", targetDb, transaction);
            if (await empty.ExecuteScalarAsync(token) is not null)
                throw new InvalidOperationException($"Identity target table {table} must be empty before copying.");
        }
        foreach (var table in Tables)
        {
            await using var metadata = new SqlCommand("""
                SELECT c.name FROM sys.columns c
                INNER JOIN sys.tables t ON t.object_id = c.object_id
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = N'identity' AND t.name = @table AND c.is_computed = 0 AND c.system_type_id <> 189
                ORDER BY c.column_id
                """, targetDb, transaction);
            metadata.Parameters.AddWithValue("@table", table);
            var columns = new List<string>();
            await using (var reader = await metadata.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
            if (columns.Count == 0) throw new InvalidOperationException($"Identity target table {table} is missing.");
            var projection = string.Join(", ", columns.Select(name => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]"));
            await using var command = new SqlCommand($"SELECT {projection} FROM [identity].[{table}]", sourceDb);
            await using var rows = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            using var copy = new SqlBulkCopy(targetDb, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints, transaction)
            {
                DestinationTableName = $"[identity].[{table}]",
                EnableStreaming = true
            };
            foreach (var column in columns) copy.ColumnMappings.Add(column, column);
            await copy.WriteToServerAsync(rows, token);
        }
        await transaction.CommitAsync(token);
    }
}
