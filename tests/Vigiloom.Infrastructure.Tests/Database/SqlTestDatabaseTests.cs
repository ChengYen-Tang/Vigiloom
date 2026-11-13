using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SqlTestDatabaseTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    public async Task LocalDbDdl_UsesSessionLockAndDoesNotBlockInitialization()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VIGILOOM_TEST_SQL_CONNECTION") is not null)
        { Assert.Inconclusive("Requires the shared Windows LocalDB fixture."); return; }
        var server = TestDatabaseServer.LocalDb();
        var master = new SqlConnectionStringBuilder { DataSource = TestDatabaseServer.LocalDbSource, InitialCatalog = "master", IntegratedSecurity = true, Pooling = false, Encrypt = SqlConnectionEncryptOption.Optional };
        await using var holder = new SqlConnection(master.ConnectionString);
        await holder.OpenAsync(TestContext.CancellationToken);
        await using var command = holder.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = N'Vigiloom.Testing.LocalDb.Ddl', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 10000; SELECT @result;";
        Assert.IsGreaterThanOrEqualTo(0, (int)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken))
        {
            var waiting = SqlTestDatabase.CreateAsync(server, timeout.Token);
            Exception? observationFailure = null;
            try
            {
                var runId = Path.GetFileName(Path.GetDirectoryName(SqlTestDatabase.RunManifestPath)!);
                // Another testhost may be ahead of this request in SQL Server's lock queue,
                // so its immediate blocker need not be our original lock-holding session.
                command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_sessions s JOIN sys.dm_tran_locks w ON w.request_session_id = s.session_id JOIN sys.dm_tran_locks h ON h.request_session_id = @@SPID AND h.resource_type = w.resource_type AND h.resource_database_id = w.resource_database_id AND h.resource_description = w.resource_description WHERE s.program_name = @program AND w.resource_type = 'APPLICATION' AND w.request_status = 'WAIT' AND h.request_status = 'GRANT'";
                command.Parameters.AddWithValue("@program", $"Vigiloom.Testing.{runId}");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                while ((int)(await command.ExecuteScalarAsync(deadline.Token))! == 0) await Task.Delay(50, deadline.Token);
            }
            catch (Exception ex) { observationFailure = ex; }
            finally { timeout.Cancel(); }
            try
            {
                await using var unexpected = await waiting;
                Assert.Fail("Database creation unexpectedly completed while the DDL lock was held.");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            catch (Exception ex) when (observationFailure is not null) { throw new AggregateException(observationFailure, ex); }
            if (observationFailure is not null) ExceptionDispatchInfo.Capture(observationFailure).Throw();
        }
        await holder.CloseAsync();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = SqlTestDatabase.CreateAsync(server, async (_, token) => { started.TrySetResult(); await release.Task.WaitAsync(token); }, TestContext.CancellationToken);
        SqlTestDatabase? second = null;
        Exception? failure = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
            second = await SqlTestDatabase.CreateAsync(server, TestContext.CancellationToken);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            release.TrySetResult();
            List<Exception> errors = [];
            if (failure is not null) errors.Add(failure);
            if (second is not null) { try { await second.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); } }
            try { if (await first is { } database) await database.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("LocalDB DDL test or cleanup failed.", errors);
        }
    }

    [AssemblyCleanup]
    public static async Task VerifyRunCleanup()
    {
        List<Exception> failures = [];
        try { await ReusableLogQuotaDatabase.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        try { await ReusableOutboxDatabase.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        try { SqlTestDatabase.CompleteRun(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Test database cleanup failed.", failures);
    }

    [TestMethod]
    public async Task CreateAndDispose_UsesOwnedDatabaseAndRemovesIt()
    {
        var server = TestDatabaseServer.FromEnvironment();
        string? name = null;
        string? directory = null;
        SqlConnectionStringBuilder? master = null;
        await SqlTestDatabase.ExecuteAsync(server, async (database, token) =>
        {
            name = database.DatabaseName;
            var manifest = database.ManifestPath;
            directory = Path.GetDirectoryName(manifest)!;
            master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME()";
            Assert.AreEqual(name, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), compatibility_level FROM sys.databases WHERE name = DB_NAME()";
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                TestContext.WriteLine($"SQL Server {reader.GetString(0)}, compatibility level {reader.GetByte(1)}");
            }
            command.CommandText = "CREATE TABLE dbo.FixtureProbe (Id int NOT NULL PRIMARY KEY); INSERT INTO dbo.FixtureProbe VALUES (1)";
            await command.ExecuteNonQueryAsync(token);
            Assert.IsTrue(File.Exists(manifest));
        }, TestContext.CancellationToken);

        Assert.IsNotNull(directory);
        Assert.IsNotNull(master);
        Assert.IsNotNull(name);
        Assert.IsFalse(Directory.Exists(directory));
        await using var masterConnection = new SqlConnection(master.ConnectionString);
        await masterConnection.OpenAsync(TestContext.CancellationToken);
        await using var check = masterConnection.CreateCommand();
        check.CommandText = "SELECT DB_ID(@name)";
        check.Parameters.AddWithValue("@name", name);
        Assert.AreEqual(DBNull.Value, await check.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Recovery_RejectsLiveOwner_AndParallelDatabasesAreIndependent()
    {
        var server = TestDatabaseServer.FromEnvironment();
        var creations = new[] { SqlTestDatabase.CreateAsync(server, TestContext.CancellationToken),
            SqlTestDatabase.CreateAsync(server, TestContext.CancellationToken) };
        Exception? failure = null;
        try
        {
            var databases = await Task.WhenAll(creations);
            Assert.AreNotEqual(databases[0].DatabaseName, databases[1].DatabaseName);
            Assert.IsTrue(new SqlConnectionStringBuilder(databases[0].ConnectionString).Pooling);
            await using (var first = new SqlConnection(databases[0].ConnectionString))
            {
                await first.OpenAsync(TestContext.CancellationToken);
            }
            Guid secondConnectionId;
            await using (var second = new SqlConnection(databases[1].ConnectionString))
            {
                await second.OpenAsync(TestContext.CancellationToken);
                secondConnectionId = second.ClientConnectionId;
            }
            await Assert.ThrowsAsync<IOException>(async () => await SqlTestDatabase.RecoverAsync(databases[0].ManifestPath, server));
            await databases[0].DisposeAsync();
            await using var connection = new SqlConnection(databases[1].ConnectionString);
            await connection.OpenAsync(TestContext.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME()";
            Assert.AreEqual(databases[1].DatabaseName, await command.ExecuteScalarAsync(TestContext.CancellationToken));
            Assert.AreEqual(secondConnectionId, connection.ClientConnectionId);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (var creation in creations.Where(task => task.IsCompletedSuccessfully))
            {
                try { await creation.Result.DisposeAsync(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (cleanupFailures.Count != 0)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                throw new AggregateException("Test and/or database cleanup failed.", cleanupFailures);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public async Task ConcurrentDatabaseInitializersCanRunTogether()
    {
        var server = TestDatabaseServer.FromEnvironment();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        async Task Initialize(SqlTestDatabase _, CancellationToken token)
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
        }
        var creations = new[] { SqlTestDatabase.CreateAsync(server, Initialize, TestContext.CancellationToken),
            SqlTestDatabase.CreateAsync(server, Initialize, TestContext.CancellationToken) };
        Exception? failure = null;
        try
        {
            var databases = await Task.WhenAll(creations);
            Assert.AreEqual(2, entered);
            Assert.AreNotEqual(databases[0].DatabaseName, databases[1].DatabaseName);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            bothEntered.TrySetResult();
            try { await Task.WhenAll(creations); } catch (Exception) { }
            var errors = new List<Exception>();
            foreach (var creation in creations.Where(task => task.IsCompletedSuccessfully))
            {
                try { await creation.Result.DisposeAsync(); }
                catch (Exception exception) { errors.Add(exception); }
            }
            if (errors.Count > 0)
            {
                if (failure is not null) errors.Insert(0, failure);
                throw new AggregateException("Test and/or database cleanup failed.", errors);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public async Task Create_WithCanceledToken_CleansPartialInitialization()
    {
        var server = TestDatabaseServer.FromEnvironment();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await SqlTestDatabase.CreateAsync(server, canceled.Token));
    }

    [TestMethod]
    public void IsolatedServer_RejectsDevelopmentCatalog()
    {
        Assert.Throws<ArgumentException>(() => TestDatabaseServer.IsolatedServer(
            "Server=localhost;Database=Vigiloom_Development;Integrated Security=true", "localhost"));
    }

    [TestMethod]
    public async Task TestFailure_DisposesOwnedDatabase()
    {
        var server = TestDatabaseServer.FromEnvironment();
        string? manifest = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(FailWithLeaseAsync);
        Assert.IsNotNull(manifest);
        Assert.IsFalse(File.Exists(manifest));

        async Task FailWithLeaseAsync()
        {
            await SqlTestDatabase.ExecuteAsync(server, (database, _) =>
            {
                manifest = database.ManifestPath;
                throw new InvalidOperationException("Simulated assertion failure");
            }, TestContext.CancellationToken);
        }
    }

    [TestMethod]
    public async Task Create_InitializerFailsAfterCreate_CleansDatabase()
    {
        var server = TestDatabaseServer.FromEnvironment();
        string? manifest = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(InitializeAsync);
        Assert.IsNotNull(manifest);
        Assert.IsFalse(File.Exists(manifest));

        async Task InitializeAsync() => await SqlTestDatabase.CreateAsync(server, (database, _) =>
        {
            manifest = database.ManifestPath;
            throw new InvalidOperationException("Initialization failed");
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Create_CanceledAfterCreate_CleansDatabase()
    {
        var server = TestDatabaseServer.FromEnvironment();
        using var canceled = new CancellationTokenSource();
        string? manifest = null;
        await Assert.ThrowsAsync<OperationCanceledException>(InitializeAsync);
        Assert.IsNotNull(manifest);
        Assert.IsFalse(File.Exists(manifest));

        async Task InitializeAsync() => await SqlTestDatabase.CreateAsync(server, async (database, token) =>
        {
            manifest = database.ManifestPath;
            await canceled.CancelAsync();
            token.ThrowIfCancellationRequested();
        }, canceled.Token);
    }

    [TestMethod]
    public async Task Execute_TestAndCleanupFail_PreservesBothAndAllowsRetry()
    {
        var server = TestDatabaseServer.FromEnvironment();
        SqlTestDatabase? owned = null;
        string? blocker = null;
        try
        {
            var error = await Assert.ThrowsExactlyAsync<AggregateException>(FailAsync);
            Assert.HasCount(2, error.InnerExceptions);
            Assert.IsInstanceOfType<InvalidOperationException>(error.InnerExceptions[0]);
            Assert.IsInstanceOfType<IOException>(error.InnerExceptions[1]);
            Assert.IsNotNull(owned);
            Assert.IsTrue(File.Exists(owned.ManifestPath));
        }
        finally
        {
            if (blocker is not null) File.Delete(blocker);
            if (owned is not null) await owned.DisposeAsync();
        }

        async Task FailAsync() => await SqlTestDatabase.ExecuteAsync(server, async (database, token) =>
        {
            owned = database;
            blocker = Path.Combine(Path.GetDirectoryName(database.ManifestPath)!, "blocker");
            await File.WriteAllTextAsync(blocker, "test", token);
            throw new InvalidOperationException("Simulated test failure");
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task EachDataRow_GetsItsOwnDatabase(int marker)
    {
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.DataRowProbe (Id int NOT NULL); INSERT INTO dbo.DataRowProbe VALUES (@marker); SELECT Id FROM dbo.DataRowProbe";
            command.Parameters.AddWithValue("@marker", marker);
            Assert.AreEqual(marker, await command.ExecuteScalarAsync(token));
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    [DataRow("DatabaseName", "master")]
    [DataRow("DatabaseName", "Vigiloom_Development")]
    [DataRow("DataSource", "unexpected-server")]
    [DataRow("DirectoryPath", "unexpected-directory")]
    public async Task Recovery_WithAlteredManifest_RefusesCleanup(string field, string value)
    {
        var server = TestDatabaseServer.FromEnvironment();
        await SqlTestDatabase.ExecuteAsync(server, async (database, token) =>
        {
            var original = await File.ReadAllTextAsync(database.ManifestPath, token);
            try
            {
                var json = JsonNode.Parse(original)!.AsObject();
                json[field] = value;
                await File.WriteAllTextAsync(database.ManifestPath, json.ToJsonString(), token);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await SqlTestDatabase.RecoverAsync(database.ManifestPath, server));
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT DB_NAME()";
                Assert.AreEqual(database.DatabaseName, await command.ExecuteScalarAsync(token));
            }
            finally
            {
                await File.WriteAllTextAsync(database.ManifestPath, original, CancellationToken.None);
            }
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Recovery_AfterAbruptOwnerExit_RemovesOnlyItsDatabase()
    {
        var server = TestDatabaseServer.FromEnvironment();
        await SqlTestDatabase.ExecuteAsync(server, async (sentinel, token) =>
        {
            await using var sentinelConnection = new SqlConnection(sentinel.ConnectionString);
            await sentinelConnection.OpenAsync(token);
            await using var sentinelCommand = sentinelConnection.CreateCommand();
            sentinelCommand.CommandText = "CREATE TABLE dbo.Sentinel (Id int NOT NULL PRIMARY KEY); INSERT INTO dbo.Sentinel VALUES (42)";
            await sentinelCommand.ExecuteNonQueryAsync(token);
            var masterBuilder = new SqlConnectionStringBuilder(sentinel.ConnectionString) { InitialCatalog = "master", Pooling = false };
            await using var master = new SqlConnection(masterBuilder.ConnectionString);
            await master.OpenAsync(token);
            await using var catalog = master.CreateCommand();
            catalog.CommandText = "SELECT DB_ID('master'), DB_ID('model'), DB_ID('msdb'), DB_ID('tempdb'), DB_ID('Vigiloom_Development')";
            var before = new object[5];
            await using (var reader = await catalog.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                reader.GetValues(before);
            }
            using var child = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { typeof(SqlTestDatabase).Assembly.Location, "hold" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start the fixture child process.");

            string? manifest = null;
            string? runManifest = null;
            string? databaseName = null;
            try
            {
                using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                runManifest = await child.StandardOutput.ReadLineAsync(handshakeTimeout.Token);
                Assert.IsNotNull(runManifest, "Child did not report its run manifest.");
                await child.StandardInput.WriteLineAsync("start");
                await child.StandardInput.FlushAsync(TestContext.CancellationToken);
                manifest = await child.StandardOutput.ReadLineAsync(TestContext.CancellationToken);
                Assert.IsNotNull(manifest, "Child did not create a manifest.");
                using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, TestContext.CancellationToken)))
                    databaseName = json.RootElement.GetProperty("DatabaseName").GetString();
                Assert.IsNotNull(databaseName);
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(TestContext.CancellationToken);
                await SqlTestDatabase.RecoverRunAsync(runManifest, server);
                Assert.IsFalse(File.Exists(manifest));
                await using var check = master.CreateCommand();
                check.CommandText = "SELECT DB_ID(@name)";
                check.Parameters.AddWithValue("@name", databaseName);
                Assert.AreEqual(DBNull.Value, await check.ExecuteScalarAsync(TestContext.CancellationToken));
                sentinelCommand.CommandText = "SELECT Id FROM dbo.Sentinel";
                Assert.AreEqual(42, await sentinelCommand.ExecuteScalarAsync(token));
                await using var after = await catalog.ExecuteReaderAsync(token);
                Assert.IsTrue(await after.ReadAsync(token));
                for (var i = 0; i < before.Length; i++) Assert.AreEqual(before[i], after.GetValue(i));
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }

                if (runManifest is not null && Directory.Exists(Path.GetDirectoryName(runManifest)))
                {
                    var runLock = Path.Combine(Path.GetDirectoryName(runManifest)!, "run.lock");
                    if (File.Exists(runLock)) await SqlTestDatabase.RecoverRunAsync(runManifest, server);
                }
            }
        }, TestContext.CancellationToken);
    }
}
