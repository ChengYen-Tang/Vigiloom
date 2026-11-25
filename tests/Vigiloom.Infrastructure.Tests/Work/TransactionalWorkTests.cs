using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Temporalio.Client;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Work;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TransactionalWorkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RedeliveryAcrossWorkflowExecutionsDoesNotRepeatSqlEffect()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");
        var temporal = await TemporalClient.ConnectAsync(new(address));
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "CREATE TABLE scheduling.TestWorkEffects (Value int NOT NULL)";
                await setup.ExecuteNonQueryAsync(token);
            }
            var handler = new TransactionalWorkHandler(database.ConnectionString, async (_, db, transaction, ct) =>
            {
                await using var insert = db.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO scheduling.TestWorkEffects VALUES (1)";
                await insert.ExecuteNonQueryAsync(ct);
                return "done";
            });
            var id = Guid.CreateVersion7().ToString("D");
            var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
            var work = new ServiceWork("inventory", "device-123", id);
            using var worker = ServiceWorkWorker.Create(temporal, queue, handler);
            await worker.ExecuteAsync(async () =>
            {
                var producer = new ServiceWorkClient(temporal);
                var first = await producer.StartAsync(id, queue, work, token);
                Assert.AreEqual("done", await first.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token));
                var repeated = await producer.StartAsync(Guid.CreateVersion7().ToString("D"), queue, work, token);
                Assert.AreEqual("done", await repeated.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token));
            }, token);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM scheduling.TestWorkEffects";
            Assert.AreEqual(1, (int)(await count.ExecuteScalarAsync(token))!);
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    public Task DuplicateDeliveryAndFailureDoNotDuplicateBusinessChanges() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "CREATE TABLE scheduling.TestWorkEffects (Value int NOT NULL)";
                await setup.ExecuteNonQueryAsync(token);
            }

            var fail = true;
            var handler = new TransactionalWorkHandler(database.ConnectionString, async (_, db, transaction, ct) =>
            {
                await using var insert = db.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO scheduling.TestWorkEffects VALUES (1)";
                await insert.ExecuteNonQueryAsync(ct);
                if (fail) throw new InvalidOperationException("Failed work");
                return "done";
            });
            var work = new ServiceWork("inventory", "device-123", Guid.CreateVersion7().ToString("D"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(work, token));
            fail = false;
            var results = await Task.WhenAll(handler.ExecuteAsync(work, token), handler.ExecuteAsync(work, token));
            CollectionAssert.AreEqual(new[] { "done", "done" }, results);
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(work with { Payload = "different" }, token));
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM scheduling.TestWorkEffects";
            Assert.AreEqual(1, (int)(await count.ExecuteScalarAsync(token))!);
        }, TestContext.CancellationToken);
}
