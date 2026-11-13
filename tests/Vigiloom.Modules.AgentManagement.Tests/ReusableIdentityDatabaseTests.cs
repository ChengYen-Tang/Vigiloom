using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Tests.Fixtures;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableIdentityDatabaseTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CasesRemainIndependentInDifferentOrders(bool reverse)
    {
        var labels = new[] { "first", "second", "third", "fourth" };
        if (reverse) Array.Reverse(labels);
        foreach (var label in labels)
        {
            await ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
            {
                await AssertCleanAsync(connection, token);
                await using var db = Resources(connection);
                db.Devices.Add(new Device(label, "127.0.0.1", true, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(token);
            }, context.CancellationToken);
        }
        await ReusableIdentityDatabase.ExecuteAsync(AssertCleanAsync, context.CancellationToken);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SnapshotCasesRemainIndependentAndSeparateFromOrdinaryReads(bool reverse)
    {
        string? snapshotName = null;
        var labels = new[] { "first", "second", "third" };
        if (reverse) Array.Reverse(labels);
        foreach (var label in labels)
        {
            await ReusableIdentityDatabase.ExecuteSnapshotAsync(async (connection, token) =>
            {
                var name = new SqlConnectionStringBuilder(connection).InitialCatalog;
                snapshotName ??= name;
                Assert.AreEqual(snapshotName, name);
                await AssertCleanAsync(connection, token);
                await AssertReadModeAsync(connection, true, token);
                await using var db = Resources(connection);
                db.Devices.Add(new Device(label, "127.0.0.1", true, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(token);
            }, context.CancellationToken);
        }
        await ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            Assert.AreNotEqual(snapshotName, new SqlConnectionStringBuilder(connection).InitialCatalog);
            await AssertCleanAsync(connection, token);
            await AssertReadModeAsync(connection, false, token);
        }, context.CancellationToken);
        await ReusableIdentityDatabase.ExecuteSnapshotAsync(AssertCleanAsync, context.CancellationToken);
    }

    [TestMethod]
    public async Task ChangedSnapshotMode_IsRejectedAndDatabaseIsNotReused()
    {
        string? rejected = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableIdentityDatabase.ExecuteSnapshotAsync(async (connectionString, token) =>
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            rejected = builder.InitialCatalog;
            builder.InitialCatalog = "master";
            builder.Pooling = false;
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{rejected}] SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE";
            await command.ExecuteNonQueryAsync(token);
        }, context.CancellationToken));
        await ReusableIdentityDatabase.ExecuteSnapshotAsync(async (connection, token) =>
        {
            Assert.AreNotEqual(rejected, new SqlConnectionStringBuilder(connection).InitialCatalog);
            await AssertCleanAsync(connection, token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task Reset_RemovesDataAfterFailureAndRetainsMigrationSeed()
    {
        string? firstDatabase = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            firstDatabase = new SqlConnectionStringBuilder(connection).InitialCatalog;
            await AssertCleanAsync(connection, token);
            await using var db = Resources(connection);
            db.Devices.Add(new Device("fixture failure", "127.0.0.1", true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
            throw new InvalidOperationException("Expected test failure");
        }, context.CancellationToken));

        await ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            Assert.AreEqual(firstDatabase, new SqlConnectionStringBuilder(connection).InitialCatalog);
            await AssertCleanAsync(connection, token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task ChangedPolicySeed_IsRejectedAndDatabaseIsNotReused()
    {
        string? rejected = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableIdentityDatabase.ExecuteAsync(async (connectionString, token) =>
        {
            rejected = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent.ReplayPolicies SET ReceiptRetentionDays = ReceiptRetentionDays + 1";
            await command.ExecuteNonQueryAsync(token);
        }, context.CancellationToken));

        await ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            Assert.AreNotEqual(rejected, new SqlConnectionStringBuilder(connection).InitialCatalog);
            await AssertCleanAsync(connection, token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task CancelledAction_IsResetBeforeNextCase()
    {
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            await using var db = Resources(connection);
            db.Devices.Add(new Device("cancelled", "127.0.0.1", true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
            cancelled.Cancel();
            token.ThrowIfCancellationRequested();
        }, cancelled.Token));
        await ReusableIdentityDatabase.ExecuteAsync(AssertCleanAsync, context.CancellationToken);
    }

    [TestMethod]
    public async Task ResetAndDisposeFailure_NeverReusesContaminatedDatabase()
    {
        string? rejected = null;
        await Assert.ThrowsExactlyAsync<AggregateException>(() => ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            rejected = new SqlConnectionStringBuilder(connection).InitialCatalog;
            await using var db = Resources(connection);
            db.Devices.Add(new Device("contaminated", "127.0.0.1", true, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
        }, context.CancellationToken,
        _ => Task.FromException(new InvalidOperationException("Simulated reset failure")),
        _ => ValueTask.FromException(new InvalidOperationException("Simulated disposal failure"))));

        await ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            Assert.AreNotEqual(rejected, new SqlConnectionStringBuilder(connection).InitialCatalog);
            await AssertCleanAsync(connection, token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task WaitingLease_CanBeCancelledWithoutDisturbingCurrentCase()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ReusableIdentityDatabase.ExecuteAsync(async (connection, token) =>
        {
            await AssertCleanAsync(connection, token);
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, context.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(context.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var second = ReusableIdentityDatabase.ExecuteAsync((_, _) => Task.CompletedTask, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        }
        finally { release.TrySetResult(); await first; }
        await ReusableIdentityDatabase.ExecuteAsync(AssertCleanAsync, context.CancellationToken);
    }

    private static ResourcesDbContext Resources(string connection) => new(new DbContextOptionsBuilder<ResourcesDbContext>()
        .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options);

    private static async Task AssertReadModeAsync(string connectionString, bool expected, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
        Assert.AreEqual(expected, await command.ExecuteScalarAsync(token));
    }

    private static async Task AssertCleanAsync(string connectionString, CancellationToken token)
    {
        await using var resources = Resources(connectionString);
        Assert.AreEqual(0, await resources.Devices.CountAsync(token));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent.ReplayPolicies;";
        Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        command.CommandText = "SELECT COUNT(*) FROM agent.__EFMigrationsHistory;";
        Assert.IsGreaterThan(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    }
}
