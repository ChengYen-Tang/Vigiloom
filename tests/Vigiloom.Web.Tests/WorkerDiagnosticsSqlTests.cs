using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class WorkerDiagnosticsSqlTests(TestContext testContext)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Slots_ConcurrentClaimsCapacityAndExpiredOwners_AreEnforced(bool snapshot) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT {(snapshot ? "ON" : "OFF")}";
                await command.ExecuteNonQueryAsync(token);
            }
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            await using var provider = services.BuildServiceProvider();
            var contexts = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var store = new WorkerDiagnosticsStore(contexts);
            var ids = Enumerable.Range(0, 64).Select(_ => Guid.CreateVersion7()).ToArray();
            var initial = await Task.WhenAll(store.ClaimAsync(ids[0], token), store.ClaimAsync(ids[1], token));
            Assert.IsNotNull(initial[0]); Assert.IsNotNull(initial[1]); Assert.AreNotEqual(initial[0], initial[1]);
            for (var index = 2; index < ids.Length; index++) Assert.IsNotNull(await store.ClaimAsync(ids[index], token));
            Assert.IsNull(await store.ClaimAsync(Guid.CreateVersion7(), token));

            var now = DateTimeOffset.UtcNow;
            var observation = new WorkerObservation(new(now, now, 2, ScannerFailureKind.Timeout), new(null, null, 0));
            Assert.IsTrue(await store.UpdateAsync(initial[0]!.Value, ids[0], observation, token));
            var snapshots = await store.GetSnapshotAsync(token);
            Assert.HasCount(64, snapshots.Workers);
            var updated = snapshots.Workers.Single(x => x.BootId == ids[0]);
            Assert.AreEqual(ScannerFailureKind.Timeout, updated.Observation.Ping.FailureKind);
            Assert.IsTrue(updated.ObservedAt <= snapshots.ObservedAt);
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE operations.WorkerDiagnosticSlots SET LeaseUntil = DATEADD(second, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE BootId = @boot";
                command.Parameters.AddWithValue("@boot", ids[0]);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }
            Assert.IsFalse(await store.UpdateAsync(initial[0]!.Value, ids[0], observation, token));
            var replacement = Guid.CreateVersion7();
            Assert.AreEqual(initial[0], await store.ClaimAsync(replacement, token));
            Assert.IsFalse(await store.UpdateAsync(initial[0]!.Value, ids[0], observation, token));
            Assert.IsTrue(await store.UpdateAsync(initial[0]!.Value, replacement, observation, token));
        }, testContext.CancellationToken);
}
