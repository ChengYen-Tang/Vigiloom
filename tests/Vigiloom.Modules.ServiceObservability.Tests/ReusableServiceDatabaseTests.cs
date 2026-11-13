using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Tests.Fixtures;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableServiceDatabaseTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reset_CasesAreIndependentInEitherOrder(bool reverse)
    {
        var names = new[] { "first", "second", "third" };
        if (reverse) Array.Reverse(names);
        foreach (var name in names)
            await ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
            {
                await using var db = Open(database.ConnectionString);
                Assert.AreEqual(0, await db.Sources.CountAsync(token));
                db.Sources.Add(new ServiceSource(name, new byte[32], DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(token);
            }, context.CancellationToken);
        await AssertEmptyAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadModes_UseSeparateDatabasesAndResetInEitherOrder(bool reverse)
    {
        var modes = reverse ? new[] { true, false, true, false } : new[] { false, true, false, true };
        var names = new Dictionary<bool, string>();
        foreach (var mode in modes)
            await ReusableServiceDatabase.ExecuteForReadModeAsync(async (database, token) =>
            {
                if (!names.TryAdd(mode, database.DatabaseName)) Assert.AreEqual(names[mode], database.DatabaseName);
                await using (var connection = new SqlConnection(database.ConnectionString))
                {
                    await connection.OpenAsync(token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
                    Assert.AreEqual(mode, await command.ExecuteScalarAsync(token));
                }
                await using var db = Open(database.ConnectionString);
                Assert.AreEqual(0, await db.Sources.CountAsync(token));
                db.Sources.Add(new ServiceSource("source", new byte[32], DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(token);
            }, mode, context.CancellationToken);
        Assert.AreNotEqual(names[false], names[true]);
    }

    [TestMethod]
    public async Task ChangedSnapshotMode_DiscardsDatabase()
    {
        string? rejected = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableServiceDatabase.ExecuteForReadModeAsync(async (database, token) =>
        {
            rejected = database.DatabaseName;
            var master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{database.DatabaseName}] SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE";
            await command.ExecuteNonQueryAsync(token);
        }, true, context.CancellationToken));
        await ReusableServiceDatabase.ExecuteForReadModeAsync((database, _) =>
        {
            Assert.AreNotEqual(rejected, database.DatabaseName);
            return Task.CompletedTask;
        }, true, context.CancellationToken);
    }

    [TestMethod]
    public async Task FailedAndCancelledCases_AreReset()
    {
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            await using var db = Open(database.ConnectionString);
            db.Sources.Add(new ServiceSource("cancelled", new byte[32], DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
            cancelled.Cancel();
            token.ThrowIfCancellationRequested();
        }, cancelled.Token));
        await AssertEmptyAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            await using var db = Open(database.ConnectionString);
            db.Sources.Add(new ServiceSource("failed", new byte[32], DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
            throw new InvalidOperationException("Expected failure");
        }, context.CancellationToken));
        await AssertEmptyAsync();
    }

    [TestMethod]
    public async Task OperationsAudit_IsResetAndSeedsArePreserved()
    {
        for (var pass = 0; pass < 2; pass++)
            await ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
            {
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM [operations].[AuditEvents]";
                Assert.AreEqual(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT COUNT(*) FROM [operations].[WorkerDiagnosticSlots]";
                Assert.AreEqual(64, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT COUNT(*) FROM [operations].[__EFMigrationsHistory]";
                Assert.IsGreaterThan(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "INSERT INTO [operations].[AuditEvents] ([ActorId],[ResourceId],[OccurredAt],[Kind],[Details]) VALUES (@id,@id,SYSUTCDATETIME(),1,N'{}')";
                command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }, context.CancellationToken);
    }

    [TestMethod]
    public async Task ChangedOperationsSeed_DiscardsDatabase()
    {
        string? prior = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            prior = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [operations].[WorkerDiagnosticSlots] SET [PingFailures] = [PingFailures] + 1 WHERE [Slot] = 64";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
        }, context.CancellationToken));
        await ReusableServiceDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task SeedChangesAndResetFailure_DiscardDatabase()
    {
        string? prior = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            prior = database.DatabaseName;
            await using var db = Open(database.ConnectionString);
            await db.Database.ExecuteSqlRawAsync("UPDATE [service].[ReceptionRejections] SET [ObservedCount] = 1 WHERE [Kind] = 1", token);
        }, context.CancellationToken));
        await ReusableServiceDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            prior = database.DatabaseName;
            return Task.CompletedTask;
        }, context.CancellationToken);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => ReusableServiceDatabase.ExecuteAsync((_, _) => Task.CompletedTask,
            context.CancellationToken, _ => Task.FromException(new InvalidOperationException("reset")),
            _ => ValueTask.FromException(new InvalidOperationException("dispose"))));
        await ReusableServiceDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
        await AssertEmptyAsync();
    }

    private Task AssertEmptyAsync() => ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
    {
        await using var db = Open(database.ConnectionString);
        Assert.AreEqual(0, await db.Sources.CountAsync(token));
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [service].[ReceptionRejections] WHERE [ObservedCount] = 0";
        Assert.AreEqual(3, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        command.CommandText = "SELECT COUNT(*) FROM [service].[__EFMigrationsHistory]";
        Assert.IsGreaterThan(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    }, context.CancellationToken);

    private static ServiceObservabilityDbContext Open(string connection) => new(new DbContextOptionsBuilder<ServiceObservabilityDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options);
}
