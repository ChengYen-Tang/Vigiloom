using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Vigiloom.Testing.SqlServer;

/// <summary>A single test-owned SQL database and a durable, exclusively held ownership manifest.</summary>
public sealed class SqlTestDatabase : IAsyncDisposable
{
    private static readonly string RunId = Guid.CreateVersion7().ToString("N");
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomSqlTests"));
    private static readonly object RunGate = new();
    private static readonly SemaphoreSlim LocalDbDdlGate = new(1, 1);
    private static FileStream? runLease;
    private readonly TestDatabaseServer server;
    private readonly DatabaseManifest manifest;
    private readonly string manifestPath;
    private FileStream? lease;
    private bool disposed;
    private bool createAttempted;

    private SqlTestDatabase(TestDatabaseServer server, DatabaseManifest manifest, string manifestPath, FileStream lease)
    {
        this.server = server;
        this.manifest = manifest;
        this.manifestPath = manifestPath;
        this.lease = lease;
    }

    public string ConnectionString => server.ForDatabase(manifest.DatabaseName);
    public string DatabaseName => manifest.DatabaseName;
    public string ManifestPath => manifestPath;
    public static string RunManifestPath => Path.Combine(Root, RunId, "run.json");

    public static Task<SqlTestDatabase> CreateAsync(TestDatabaseServer server, CancellationToken cancellationToken = default)
        => CreateCoreAsync(server, null, cancellationToken);

    public static Task<SqlTestDatabase> CreateAsync(TestDatabaseServer server,
        Func<SqlTestDatabase, CancellationToken, Task> initialize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        return CreateCoreAsync(server, initialize, cancellationToken);
    }

    /// <summary>Preserves both the test failure and a cleanup failure.</summary>
    public static async Task ExecuteAsync(TestDatabaseServer server,
        Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var database = await CreateAsync(server, cancellationToken);
        Exception? failure = null;
        try { await action(database, cancellationToken); }
        catch (Exception exception) { failure = exception; }

        try { await database.DisposeAsync(); }
        catch (Exception cleanupFailure)
        {
            if (failure is not null) throw new AggregateException("Test and cleanup both failed.", failure, cleanupFailure);
            throw;
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task<SqlTestDatabase> CreateCoreAsync(TestDatabaseServer server,
        Func<SqlTestDatabase, CancellationToken, Task>? initialize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        EnsureRun();
        var caseId = Guid.CreateVersion7().ToString("N");
        var directory = Path.Combine(Root, RunId, caseId);
        var name = $"Vigiloom_Test_{RunId}_{caseId}";
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var lockPath = Path.Combine(directory, "lease.lock");
        var lease = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        using var process = Process.GetCurrentProcess();
        var manifest = new DatabaseManifest(RunId, caseId, name, server.DataSource,
            process.Id, process.StartTime.ToUniversalTime().Ticks, directory);
        var database = new SqlTestDatabase(server, manifest, manifestPath, lease);

        try
        {
            // The manifest is durable before CREATE. An exclusive lease protects live owners from recovery.
            var temporaryManifest = manifestPath + ".tmp";
            await using (var file = new FileStream(temporaryManifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, manifest, cancellationToken: cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryManifest, manifestPath);

            await WithLocalDbDdlGateAsync(server, "create", async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT DB_ID(@database)";
                command.Parameters.AddWithValue("@database", name);
                if (await command.ExecuteScalarAsync(cancellationToken) is not DBNull)
                    throw new InvalidOperationException("Generated test database already exists; it will not be modified.");
                command.Parameters.Clear();
                database.createAttempted = true;
                if (IsLocalDb(server)) command.CommandTimeout = 60;
                command.CommandText = IsLocalDb(server)
                    ? $"CREATE DATABASE [{name}] ON PRIMARY (NAME = N'{name}', FILENAME = N'{SqlLiteral(DataFile(manifest))}') LOG ON (NAME = N'{name}_log', FILENAME = N'{SqlLiteral(LogFile(manifest))}')"
                    : $"CREATE DATABASE [{name}]";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }, cancellationToken);
            if (initialize is not null) await initialize(database, cancellationToken);
            return database;
        }
        catch (Exception failure)
        {
            try
            {
                await database.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                database.lease?.Dispose();
                database.lease = null;
                throw new AggregateException("Database initialization and cleanup both failed.", failure, cleanupFailure);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(IsLocalDb(server) ? 120 : 30));
        if (createAttempted)
        {
            await DropOwnedAsync(server, manifest, manifestPath, timeout.Token);
        }
        File.Delete(manifestPath + ".tmp");
        var lockPath = Path.Combine(manifest.DirectoryPath, "lease.lock");
        EnsureNoUnexpectedFiles(manifest.DirectoryPath, manifestPath, lockPath);
        lease?.Dispose();
        lease = null;
        File.Delete(manifestPath);
        File.Delete(lockPath);
        Directory.Delete(manifest.DirectoryPath);
        disposed = true;
    }

    /// <summary>Recovers one named manifest only after its owning process has exited.</summary>
    public static async Task RecoverAsync(string manifestPath, TestDatabaseServer server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(server);
        var fullPath = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<DatabaseManifest>(await File.ReadAllTextAsync(fullPath))
            ?? throw new InvalidDataException("Invalid database manifest.");
        ValidateManifest(server, manifest, fullPath);

        // FileShare.None prevents recovery racing a live test even if process IDs are recycled.
        using (new FileStream(Path.Combine(manifest.DirectoryPath, "lease.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (OwnerIsAlive(manifest))
            {
                throw new InvalidOperationException("The test run still owns this database.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(IsLocalDb(server) ? 120 : 30));
            await DropOwnedAsync(server, manifest, fullPath, timeout.Token);
            EnsureNoUnexpectedFiles(manifest.DirectoryPath, fullPath, Path.Combine(manifest.DirectoryPath, "lease.lock"));
            File.Delete(fullPath);
        }

        File.Delete(Path.Combine(manifest.DirectoryPath, "lease.lock"));
        Directory.Delete(manifest.DirectoryPath);
    }

    /// <summary>Checks and removes this testhost's empty run directory at assembly cleanup.</summary>
    public static void CompleteRun()
    {
        lock (RunGate)
        {
            if (runLease is null)
            {
                return;
            }

            var directory = Path.Combine(Root, RunId);
            if (Directory.EnumerateDirectories(directory).Any())
            {
                throw new InvalidOperationException($"Test databases remain in run manifest {RunManifestPath}.");
            }

            runLease.Dispose();
            runLease = null;
            File.Delete(RunManifestPath);
            File.Delete(Path.Combine(directory, "run.lock"));
            Directory.Delete(directory);
        }
    }

    /// <summary>Recovers only cases explicitly registered below a stopped run's manifest.</summary>
    public static async Task RecoverRunAsync(string runManifestPath, TestDatabaseServer server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runManifestPath);
        ArgumentNullException.ThrowIfNull(server);
        var fullPath = Path.GetFullPath(runManifestPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Invalid run directory.");
        var runId = Path.GetFileName(directory);
        if (!IsToken(runId) || !string.Equals(fullPath, Path.Combine(Root, runId, "run.json"), PathComparison))
        {
            throw new InvalidDataException("Run manifest is outside the test-owned directory.");
        }

        var errors = new List<Exception>();
        using (new FileStream(Path.Combine(directory, "run.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (!File.Exists(fullPath))
            {
                // No case creation can start before the run manifest has been atomically published.
                File.Delete(fullPath + ".tmp");
                if (Directory.EnumerateDirectories(directory).Any())
                    throw new InvalidDataException("Incomplete run contains unexpected case directories.");
            }
            else
            {
                var run = JsonSerializer.Deserialize<RunManifest>(await File.ReadAllTextAsync(fullPath))
                    ?? throw new InvalidDataException("Invalid run manifest.");
                if (run.RunId != runId) throw new InvalidDataException("Run ID does not match its directory.");
                if (ProcessIsAlive(run.OwnerProcessId, run.OwnerStartTimeUtcTicks))
                {
                    throw new InvalidOperationException("The test run is still active.");
                }

                foreach (var caseDirectory in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        var caseId = Path.GetFileName(caseDirectory);
                        if (!IsToken(caseId) || !string.Equals(Path.GetFullPath(caseDirectory), Path.Combine(directory, caseId), PathComparison))
                        {
                            throw new InvalidDataException("Unexpected case directory in run.");
                        }

                        var caseManifest = Path.Combine(caseDirectory, "manifest.json");
                        if (File.Exists(caseManifest))
                        {
                            await RecoverAsync(caseManifest, server);
                        }
                        else
                        {
                            // CREATE can only start after the complete manifest was atomically renamed.
                            var caseLock = Path.Combine(caseDirectory, "lease.lock");
                            if (File.Exists(caseLock))
                            {
                                using (new FileStream(caseLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                                {
                                    if (File.Exists(caseManifest)) throw new InvalidOperationException("Manifest appeared during recovery.");
                                    File.Delete(caseManifest + ".tmp");
                                }
                                File.Delete(caseLock);
                            }
                            Directory.Delete(caseDirectory);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add(new InvalidOperationException($"Failed to recover {caseDirectory}.", exception));
                    }
                }

                if (errors.Count != 0)
                {
                    throw new AggregateException("Some test databases could not be recovered.", errors);
                }
                File.Delete(fullPath);
            }
        }

        File.Delete(Path.Combine(directory, "run.lock"));
        Directory.Delete(directory);
    }

    private static async Task DropOwnedAsync(TestDatabaseServer server, DatabaseManifest manifest, string manifestPath, CancellationToken cancellationToken)
    {
        ValidateManifest(server, manifest, manifestPath);
        await WithLocalDbDdlGateAsync(server, "cleanup", connection => DropOwnedCoreAsync(server, manifest, connection, cancellationToken), cancellationToken);
    }

    private static async Task DropOwnedCoreAsync(TestDatabaseServer server, DatabaseManifest manifest, SqlConnection connection, CancellationToken cancellationToken)
    {
        // Only clear this test database's pool; administrative connections remain unpooled.
        using (var pooledConnection = new SqlConnection(server.ForDatabase(manifest.DatabaseName)))
        {
            SqlConnection.ClearPool(pooledConnection);
        }
        // SchedulingReadStorage uses the same owned database with a shorter connect timeout,
        // which SqlClient treats as a separate pool. Clear only that exact owned pool too.
        var managementConnection = new SqlConnectionStringBuilder(server.ForDatabase(manifest.DatabaseName)) { ConnectTimeout = 5 };
        using (var pooledManagementConnection = new SqlConnection(managementConnection.ConnectionString))
        {
            SqlConnection.ClearPool(pooledManagementConnection);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@database)";
        command.Parameters.AddWithValue("@database", manifest.DatabaseName);
        var databaseId = await command.ExecuteScalarAsync(cancellationToken);
        if (databaseId is not null and not DBNull)
        {
            if (IsLocalDb(server))
            {
                command.CommandText = "SELECT physical_name FROM sys.master_files WHERE database_id = DB_ID(@database) ORDER BY file_id";
                var actualFiles = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        actualFiles.Add(Path.GetFullPath(reader.GetString(0)));
                    }
                }

                var expectedFiles = new[] { DataFile(manifest), LogFile(manifest) };
                if (actualFiles.Count != expectedFiles.Length
                    || !actualFiles.Zip(expectedFiles).All(pair => string.Equals(pair.First, pair.Second, PathComparison)))
                {
                    throw new InvalidOperationException("Database files do not match the manifest; cleanup was refused.");
                }
            }

            command.Parameters.Clear();
            // The name is generated locally and checked against the manifest, never taken as arbitrary SQL.
            if (IsLocalDb(server)) command.CommandTimeout = 60;
            command.CommandText = $"ALTER DATABASE [{manifest.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{manifest.DatabaseName}];";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT DB_ID(@database)";
            command.Parameters.AddWithValue("@database", manifest.DatabaseName);
            if (await command.ExecuteScalarAsync(cancellationToken) is not DBNull)
            {
                throw new InvalidOperationException("Test database still exists after cleanup.");
            }
        }

        if (IsLocalDb(server))
        {
            // Only the exact files in the uniquely owned directory may be removed.
            File.Delete(DataFile(manifest));
            File.Delete(LogFile(manifest));
            if (File.Exists(DataFile(manifest)) || File.Exists(LogFile(manifest)))
            {
                throw new IOException("Test database files remain after cleanup.");
            }
        }
    }

    private static async Task WithLocalDbDdlGateAsync(TestDatabaseServer server, string operation, Func<SqlConnection, Task> action, CancellationToken token)
    {
        if (!IsLocalDb(server))
        {
            await using var remote = new SqlConnection(server.ConnectionString);
            await remote.OpenAsync(token);
            await action(remote);
            return;
        }
        var start = Stopwatch.GetTimestamp();
        await LocalDbDdlGate.WaitAsync(token);
        var entered = Stopwatch.GetTimestamp();
        try
        {
            // LocalDB is shared by testhost processes. A session lock coordinates their
            // DDL; the same unpooled master connection executes the protected operation.
            var master = new SqlConnectionStringBuilder(server.ConnectionString) { ApplicationName = $"Vigiloom.Testing.{RunId}" };
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 65;
            command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = N'Vigiloom.Testing.LocalDb.Ddl', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000; SELECT @result;";
            object? response;
            try { response = await command.ExecuteScalarAsync(token); }
            catch (SqlException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            token.ThrowIfCancellationRequested();
            var result = (int)(response ?? throw new InvalidOperationException("No LocalDB DDL lock result."));
            if (result < 0) throw new InvalidOperationException($"Could not acquire LocalDB DDL lock ({result}).");
            await action(connection);
        }
        finally
        {
            var waited = Stopwatch.GetElapsedTime(start, entered);
            var elapsed = Stopwatch.GetElapsedTime(entered);
            LocalDbDdlGate.Release();
            if (waited > TimeSpan.FromSeconds(5) || elapsed > TimeSpan.FromSeconds(5))
                Console.Error.WriteLine($"LocalDB test {operation}: wait {waited.TotalSeconds:F1}s, execution {elapsed.TotalSeconds:F1}s.");
        }
    }

    private static void ValidateManifest(TestDatabaseServer server, DatabaseManifest manifest, string manifestPath)
    {
        if (!IsToken(manifest.RunId) || !IsToken(manifest.CaseId)
            || manifest.DatabaseName != $"Vigiloom_Test_{manifest.RunId}_{manifest.CaseId}"
            || !string.Equals(manifest.DataSource, server.DataSource, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(manifest.DirectoryPath), Path.Combine(Root, manifest.RunId, manifest.CaseId), PathComparison)
            || !string.Equals(Path.GetFullPath(manifestPath), Path.Combine(Root, manifest.RunId, manifest.CaseId, "manifest.json"), PathComparison))
        {
            throw new InvalidDataException("Manifest does not describe a test-owned database on the selected server.");
        }
    }

    private static bool IsToken(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty
        && value == id.ToString("N", CultureInfo.InvariantCulture);

    private static void EnsureNoUnexpectedFiles(string directory, string manifestPath, string lockPath)
    {
        if (Directory.EnumerateFileSystemEntries(directory)
            .Any(path => !string.Equals(path, manifestPath, PathComparison) && !string.Equals(path, lockPath, PathComparison)))
        {
            throw new IOException("Unexpected files remain in the test database directory; manifest was retained.");
        }
    }

    private static bool IsLocalDb(TestDatabaseServer server) =>
        string.Equals(server.DataSource, TestDatabaseServer.LocalDbSource, StringComparison.OrdinalIgnoreCase);

    private static string DataFile(DatabaseManifest manifest) => Path.Combine(manifest.DirectoryPath, "database.mdf");
    private static string LogFile(DatabaseManifest manifest) => Path.Combine(manifest.DirectoryPath, "database_log.ldf");
    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool OwnerIsAlive(DatabaseManifest manifest)
        => ProcessIsAlive(manifest.OwnerProcessId, manifest.OwnerStartTimeUtcTicks);

    private static bool ProcessIsAlive(int processId, long startTimeUtcTicks)
    {
        try
        {
            using var owner = Process.GetProcessById(processId);
            return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void EnsureRun()
    {
        lock (RunGate)
        {
            if (runLease is not null) return;
            var directory = Path.Combine(Root, RunId);
            Directory.CreateDirectory(directory);
            var lease = new FileStream(Path.Combine(directory, "run.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                using var process = Process.GetCurrentProcess();
                var run = new RunManifest(RunId, process.Id, process.StartTime.ToUniversalTime().Ticks);
                using (var file = new FileStream(RunManifestPath + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(file, run);
                    file.Flush(flushToDisk: true);
                }
                File.Move(RunManifestPath + ".tmp", RunManifestPath);
                runLease = lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }
}
