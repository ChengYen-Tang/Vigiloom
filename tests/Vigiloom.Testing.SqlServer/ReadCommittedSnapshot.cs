using Microsoft.Data.SqlClient;

namespace Vigiloom.Testing.SqlServer;

/// <summary>Configures and checks isolation on an owned temporary test database.</summary>
public static class ReadCommittedSnapshot
{
    public static async Task ConfigureAsync(SqlTestDatabase database, bool enabled, CancellationToken token)
    {
        var master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        // DatabaseName is generated and owned by SqlTestDatabase, not supplied by a test caller.
        command.CommandText = $"ALTER DATABASE [{database.DatabaseName}] SET READ_COMMITTED_SNAPSHOT {(enabled ? "ON" : "OFF")}";
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task EnsureAsync(SqlConnection connection, bool expected, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
        if (await command.ExecuteScalarAsync(token) is not bool actual || actual != expected)
            throw new InvalidOperationException("A reusable test changed its database read-committed snapshot mode.");
    }
}
