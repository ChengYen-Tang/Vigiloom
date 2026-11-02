using Microsoft.Data.SqlClient;

namespace Vigiloom.Testing.SqlServer;

/// <summary>An explicitly selected test server; product configuration is never consulted.</summary>
public sealed class TestDatabaseServer
{
    public const string LocalDbSource = @"(localdb)\MSSQLLocalDB";

    private TestDatabaseServer(SqlConnectionStringBuilder connection)
    {
        connection.InitialCatalog = "master";
        connection.Pooling = false;
        ConnectionString = connection.ConnectionString;
        DataSource = connection.DataSource;
    }

    internal string ConnectionString { get; }
    internal string DataSource { get; }

    public static TestDatabaseServer LocalDb()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("LocalDB requires Windows; configure an isolated SQL Server for tests.");
        }

        return new(new SqlConnectionStringBuilder
        {
            DataSource = LocalDbSource,
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Optional,
            ConnectTimeout = 15
        });
    }

    /// <summary>Uses explicit test-only environment settings, or the Windows LocalDB default.</summary>
    public static TestDatabaseServer FromEnvironment()
    {
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_TEST_SQL_CONNECTION");
        var allowedSource = Environment.GetEnvironmentVariable("VIGILOOM_TEST_SQL_ALLOW_SOURCE");
        if (connection is null && allowedSource is null)
        {
            return LocalDb();
        }

        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(allowedSource))
        {
            throw new InvalidOperationException("Both test SQL connection and allowed source must be configured explicitly.");
        }

        return IsolatedServer(connection, allowedSource);
    }

    /// <summary>
    /// Selects an independently provisioned test server with a least-privilege account.
    /// The connection must explicitly target master and its source must match the allow-list.
    /// </summary>
    public static TestDatabaseServer IsolatedServer(string connectionString, string allowedDataSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedDataSource);
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(connection.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(connection.DataSource, allowedDataSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(connection.DataSource, LocalDbSource, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(connection.AttachDBFilename))
        {
            throw new ArgumentException("An explicit isolated test server and master connection are required.", nameof(connectionString));
        }

        return new(connection);
    }

    internal string ForDatabase(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = databaseName, Pooling = true };
        return builder.ConnectionString;
    }
}
