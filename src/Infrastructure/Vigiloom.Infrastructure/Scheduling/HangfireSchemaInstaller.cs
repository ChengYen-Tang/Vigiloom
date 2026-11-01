using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;

namespace Vigiloom.Infrastructure.Scheduling;

/// <summary>Explicit deployment operation, after validating the target is an existing non-system database.</summary>
public static class HangfireSchemaInstaller
{
    public static async Task InstallAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var catalog = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (string.IsNullOrWhiteSpace(catalog) || new[] { "master", "model", "msdb", "tempdb" }.Contains(catalog, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Specify an existing non-system database for Hangfire installation.");
        await using (var check = new SqlConnection(connectionString)) await check.OpenAsync(cancellationToken);
        _ = new SqlServerStorage(connectionString, SchedulingRegistration.StorageOptions(prepareSchema: true));
        // Hangfire logs and suppresses installation failures; verify before reporting success.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [Version] FROM [hangfire].[Schema]";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (version < 7) throw new InvalidOperationException($"Hangfire schema version {version} is not supported.");
    }
}
