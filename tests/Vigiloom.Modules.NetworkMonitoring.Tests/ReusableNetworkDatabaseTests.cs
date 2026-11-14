using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Tests.Fixtures;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableNetworkDatabaseTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reset_ClearsSettingsRunsAndAuditInEitherOrder(bool reverse)
    {
        var intervals = reverse ? new[] { 12, 8 } : new[] { 8, 12 };
        foreach (var interval in intervals)
            await ReusableNetworkDatabase.ExecuteAsync(async (database, token) =>
            {
                var options = Options(database.ConnectionString);
                await using (var db = new NetworkDbContext(options))
                {
                    Assert.AreEqual(0, await db.SpeedRuns.CountAsync(token));
                    Assert.AreEqual(0, await db.SpeedSettings.CountAsync(token));
                }
                var settings = new SpeedSettings(new Factory(options), new OperationAuditWriter());
                var initial = await settings.GetAsync(token);
                var updated = await settings.UpdateAsync(new SpeedSettingsInput(false, interval, 100, 45, initial.Version), Guid.CreateVersion7(), token);
                Assert.IsNotNull(updated);
                await using (var db = new NetworkDbContext(options))
                {
                    db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow, State = SpeedTestState.Queued });
                    await db.SaveChangesAsync(token);
                }
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM [operations].[AuditEvents]";
                Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
            }, context.CancellationToken);
    }

    [TestMethod]
    public async Task SnapshotDatabase_IsReusedAndIsolatedFromOrdinaryReads()
    {
        string? snapshotName = null;
        for (var attempt = 0; attempt < 2; attempt++)
            await ReusableNetworkDatabase.ExecuteSnapshotAsync(async (database, token) =>
            {
                snapshotName ??= database.DatabaseName;
                Assert.AreEqual(snapshotName, database.DatabaseName);
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await Vigiloom.Testing.SqlServer.ReadCommittedSnapshot.EnsureAsync(connection, true, token);
                await using var db = new NetworkDbContext(Options(database.ConnectionString));
                Assert.AreEqual(0, await db.SpeedRuns.CountAsync(token));
                db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow, State = SpeedTestState.Queued });
                await db.SaveChangesAsync(token);
            }, context.CancellationToken);

        await ReusableNetworkDatabase.ExecuteAsync(async (database, token) =>
        {
            Assert.AreNotEqual(snapshotName, database.DatabaseName);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await Vigiloom.Testing.SqlServer.ReadCommittedSnapshot.EnsureAsync(connection, false, token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task ChangedPreservedSeed_DiscardsDatabase()
    {
        string? previous = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableNetworkDatabase.ExecuteAsync(async (database, token) =>
        {
            previous = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [operations].[WorkerDiagnosticSlots] SET [PingFailures] = [PingFailures] + 1 WHERE [Slot] = 64";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
        }, context.CancellationToken));
        await ReusableNetworkDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(previous, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }

    private static DbContextOptions<NetworkDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;

    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext() => new(options);
    }
}
