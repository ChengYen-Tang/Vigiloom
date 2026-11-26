using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NetworkDataMigrationTests(TestContext context)
{
    [TestMethod]
    public Task CopiesHistoryAndAlignsTransitionBaseline() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outer) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            static NetworkDbContext Create(string connection) => new(new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(connection,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options);
            await using (var db = Create(source.ConnectionString)) await db.Database.MigrateAsync(token);
            await using (var db = Create(target.ConnectionString)) await db.Database.MigrateAsync(token);
            var device = Guid.CreateVersion7();
            await using (var db = Create(source.ConnectionString))
            {
                db.PingTargets.Add(new PingTargetState { DeviceId = device, Target = "example.org", DeviceVersion = new byte[8],
                    Generation = 42, LastTransitionGeneration = 12, Enabled = true, DueAt = DateTimeOffset.UtcNow, State = Reachability.Unreachable });
                db.PingStateEvents.Add(new PingStateEvent { DeviceId = device, Target = "example.org", Generation = 12,
                    OccurredAt = DateTimeOffset.UtcNow, Previous = Reachability.Reachable, Current = Reachability.Unreachable });
                await db.SaveChangesAsync(token);
            }
            await NetworkDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token);
            await using var copied = Create(target.ConnectionString);
            var state = await copied.PingTargets.SingleAsync(token);
            Assert.AreEqual(42L, state.LastTransitionGeneration);
            Assert.AreEqual(Reachability.Unreachable, state.State);
            Assert.AreEqual(1, await copied.PingStateEvents.CountAsync(token));
        }, outer), context.CancellationToken);
}
