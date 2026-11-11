using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableWebDatabaseTests(TestContext context)
{
    [TestMethod]
    public async Task FailedCase_IsResetBeforeAnotherHost()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            await using var db = Open(database.ConnectionString);
            db.Devices.Add(new Device("failed", "127.0.0.1", false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
            throw new InvalidOperationException("Expected failure");
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            await using var db = Open(database.ConnectionString);
            Assert.AreEqual(0, await db.Devices.CountAsync(token));
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
            using var client = PingTestSite.Client(host);
            await PingTestSite.LoginAsync(client, "reader", token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task RemovedTailSeed_IsRejectedAndDatabaseIsNotReused()
    {
        string? prior = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            prior = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM [operations].[WorkerDiagnosticSlots] WHERE [Slot] = 64";
            await command.ExecuteNonQueryAsync(token);
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(64)]
    public async Task ChangedSeed_IsRejectedAndDatabaseIsNotReused(int slot)
    {
        string? prior = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            prior = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [operations].[WorkerDiagnosticSlots] SET [PingFailures] = [PingFailures] + 1 WHERE [Slot] = @slot";
            command.Parameters.AddWithValue("@slot", slot);
            await command.ExecuteNonQueryAsync(token);
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }

    private static ResourcesDbContext Open(string connection) => new(new DbContextOptionsBuilder<ResourcesDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options);
}
