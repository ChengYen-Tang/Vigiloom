using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
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
                db.PingTargets.Add(new PingTargetState { DeviceId = device, Target = "example.org", DeviceVersion = [255, 255, 255, 255, 255, 255, 255, 255],
                    Generation = 42, LastTransitionGeneration = 12, Enabled = true, DueAt = DateTimeOffset.UtcNow, LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1), State = Reachability.Unreachable });
                db.PingStateEvents.Add(new PingStateEvent { DeviceId = device, Target = "example.org", Generation = 12,
                    OccurredAt = DateTimeOffset.UtcNow, Previous = Reachability.Reachable, Current = Reachability.Unreachable });
                db.PingSamples.Add(new PingSample(device, "central", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, 3));
                db.SpeedSettings.Add(new SpeedSetting { IntervalHours = 12, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
                db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow,
                    MaxMiB = 2048, TimeoutSeconds = 60, DownloadMbps = 100 });
                await db.SaveChangesAsync(token);
            }
            await NetworkDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token);
            await using var copied = Create(target.ConnectionString);
            var state = await copied.PingTargets.SingleAsync(token);
            Assert.AreEqual(42L, state.LastTransitionGeneration);
            CollectionAssert.AreEqual(new byte[8], state.DeviceVersion);
            Assert.IsNull(state.LeaseUntil);
            Assert.AreEqual(Reachability.Unreachable, state.State);
            Assert.AreEqual(1, await copied.PingStateEvents.CountAsync(token));
            Assert.AreEqual(3d, (await copied.PingSamples.SingleAsync(token)).LatencyMilliseconds);
            Assert.AreEqual(12, (await copied.SpeedSettings.SingleAsync(token)).IntervalHours);
            Assert.AreEqual(100d, (await copied.SpeedRuns.SingleAsync(token)).DownloadMbps);
        }, outer), context.CancellationToken);
}
