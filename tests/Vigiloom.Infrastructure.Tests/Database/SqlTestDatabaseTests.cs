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

    [AssemblyCleanup]
    public static void VerifyRunCleanup() => SqlTestDatabase.CompleteRun();

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
            master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master" };
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
            await Assert.ThrowsAsync<IOException>(async () => await SqlTestDatabase.RecoverAsync(databases[0].ManifestPath, server));
            await databases[0].DisposeAsync();
            await using var connection = new SqlConnection(databases[1].ConnectionString);
            await connection.OpenAsync(TestContext.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME()";
            Assert.AreEqual(databases[1].DatabaseName, await command.ExecuteScalarAsync(TestContext.CancellationToken));
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
            var masterBuilder = new SqlConnectionStringBuilder(sentinel.ConnectionString) { InitialCatalog = "master" };
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
