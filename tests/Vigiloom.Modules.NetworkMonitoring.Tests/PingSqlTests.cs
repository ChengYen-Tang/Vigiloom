using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using System.Data;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PingSqlTests
{
    public TestContext TestContext { get; set; } = null!;
    [AssemblyCleanup] public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    public Task PooledContext_ClosesConnectionsAfterPingClaimAndSpeedSettings() => WithDatabase(async (resources, network, token) =>
    {
        string connection;
        await using (var db = network.CreateDbContext()) connection = db.Database.GetDbConnection().ConnectionString;
        var services = new ServiceCollection();
        services.AddModuleSqlServer<NetworkDbContext>(connection, "network", pooled: true);
        using var provider = services.BuildServiceProvider();
        var pooled = provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        NetworkDbContext original;
        await using (var db = await pooled.CreateDbContextAsync(token)) original = db;

        var settings = new PingSettings(pooled, TimeProvider.System);
        await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var store = new PingStore(pooled, new DevicePingSource(resources), TimeProvider.System);
        await store.ClaimAsync(policy, token);
        await using (var db = await pooled.CreateDbContextAsync(token))
        {
            Assert.AreSame(original, db);
            Assert.AreEqual(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        await new SpeedSettings(pooled).GetAsync(token);
        await using (var db = await pooled.CreateDbContextAsync(token))
        {
            Assert.AreSame(original, db);
            Assert.AreEqual(ConnectionState.Closed, db.Database.GetDbConnection().State);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE network.PingTargets", token);
        }
        await Assert.ThrowsAsync<SqlException>(() => store.ClaimAsync(policy, token));
        await using var afterFailure = await pooled.CreateDbContextAsync(token);
        Assert.AreSame(original, afterFailure);
        Assert.AreEqual(ConnectionState.Closed, afterFailure.Database.GetDbConnection().State);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task LeasesSamplesTransitionsAndStaleness(bool snapshotReads) => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, other) = await SeedAsync(resources, token);
        var source = new DevicePingSource(resources);
        var store = new PingStore(network, source, clock);
        var settings = new PingSettings(network, clock);
        await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        await store.ReconcileAsync(policy, token);
        await DueAsync(network, token);
        var competing = await Task.WhenAll(store.ClaimAsync(policy, token), store.ClaimAsync(policy, token));
        var leases = competing.SelectMany(x => x).ToArray();
        Assert.HasCount(3, leases, "Both device modes and the additional target must be scheduled.");
        Assert.AreEqual(3, leases.Select(x => (x.DeviceId, x.Target)).Distinct().Count());
        foreach (var lease in leases) Assert.IsTrue(await store.CommitAsync(lease, policy, new(PingOutcome.Timeout, lease.Target, null), clock.Now, "central", token));
        foreach (var round in Enumerable.Range(0, 2))
        {
            await DueAsync(network, token);
            foreach (var lease in await store.ClaimAsync(policy, token))
                Assert.IsTrue(await store.CommitAsync(lease, policy, new(PingOutcome.Timeout, lease.Target, null), clock.Now, "central", token));
        }
        await using (var db = network.CreateDbContext())
        {
            Assert.AreEqual(9, await db.PingSamples.CountAsync(token));
            Assert.AreEqual(3, await db.PingStateEvents.CountAsync(token));
            Assert.IsTrue(await db.PingTargets.AllAsync(x => x.State == Reachability.Unreachable, token));
            Assert.IsTrue(await db.PingSamples.AllAsync(x => x.LatencyMilliseconds == null && !x.Succeeded && x.Generation != null && x.Target != null, token));
        }
        for (var round = 0; round < 2; round++)
        {
            await DueAsync(network, token);
            foreach (var lease in await store.ClaimAsync(policy, token))
                Assert.IsTrue(await store.CommitAsync(lease, policy, new(PingOutcome.Success, lease.Target, 1.25), clock.Now, "central", token));
        }
        var status = await store.GetStatusAsync(device.Id, [device.Address], Convert.ToBase64String(device.Version), token);
        Assert.AreEqual(Reachability.Reachable, status.Targets[0].State);
        Assert.AreEqual(1.25, status.Targets[0].LatencyMilliseconds);
        clock.Now = clock.Now.AddSeconds(91);
        var stale = await store.GetStatusAsync(device.Id, [device.Address], Convert.ToBase64String(device.Version), token);
        Assert.IsTrue(stale.Targets[0].Stale);
        Assert.AreEqual(Reachability.Unknown, stale.Targets[0].State);
        Assert.AreEqual("資料過期", stale.Targets[0].DisplayState);
        Assert.AreEqual(other.Id, (await store.GetStatusAsync(other.Id, [other.Address], Convert.ToBase64String(other.Version), token)).DeviceId);
    }, snapshotReads);

    [TestMethod]
    public Task FencingDeviceChangesAndSettingsConflicts() => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        var settings = new PingSettings(network, clock);
        await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        await store.ReconcileAsync(policy, token); await DueAsync(network, token);
        var original = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        await using (var db = network.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE network.PingTargets SET LeaseUntil = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
        var renewed = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        Assert.IsFalse(await store.CommitAsync(original, policy, new(PingOutcome.Success, "127.0.0.1", 0), clock.Now, "central", token));
        Assert.IsTrue(await store.CommitAsync(renewed, policy, new(PingOutcome.Success, "127.0.0.1", 0), clock.Now, "central", token));
        Assert.IsFalse(await store.CommitAsync(renewed, policy, new(PingOutcome.Success, "127.0.0.1", 0), clock.Now, "central", token));
        await DueAsync(network, token);
        var beforeChange = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Update(current.Name, "127.0.0.2", current.DeepMonitoring, true, clock.Now);
            await db.SaveChangesAsync(token);
        }
        Assert.IsFalse(await store.IsCurrentAsync(beforeChange, token));
        Assert.IsFalse(await store.CommitAsync(beforeChange, policy, new(PingOutcome.Success, "127.0.0.1", 0), clock.Now, "central", token));
        await store.ReconcileAsync(policy, token);
        await DueAsync(network, token);
        var newTarget = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        var changed = new PingSettingsInput(10, 1, 2, 1, 4, policy.Version);
        Assert.IsNotNull(await settings.UpdateAsync(changed, Guid.CreateVersion7(), token));
        Assert.IsNull(await settings.UpdateAsync(changed, Guid.CreateVersion7(), token));
        Assert.IsFalse(await store.CommitAsync(newTarget, policy, new(PingOutcome.Success, "127.0.0.2", 1), clock.Now, "central", token));
        await using var check = network.CreateDbContext();
        Assert.AreEqual(1, await check.PingSamples.CountAsync(x => x.DeviceId == device.Id, token));
        Assert.AreEqual("127.0.0.1", (await check.PingSamples.SingleAsync(x => x.DeviceId == device.Id, token)).Target);
    });

    [TestMethod]
    public Task RunnerSamplesEachConfiguredTargetExactlyOnce() => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock); await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(policy, token); await DueAsync(network, token);
        var probe = new CountingProbe();
        var runner = new PingRunner(settings, store, probe, clock);
        await runner.RunOnceAsync(token);
        Assert.AreEqual(3, probe.Calls);
        await runner.RunOnceAsync(token);
        Assert.AreEqual(3, probe.Calls, "A subsequent scan must not resend work before its due time.");
        await using var db = network.CreateDbContext();
        Assert.AreEqual(3, await db.PingSamples.CountAsync(token));
        await using (var resource = resources.CreateDbContext())
        {
            var current = await resource.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Archive(clock.Now); await resource.SaveChangesAsync(token);
        }
        await DueAsync(network, token);
        await runner.RunOnceAsync(token);
        Assert.AreEqual(5, probe.Calls, "Archiving one device must stop only its own target.");
    });

    [TestMethod]
    [DataRow("disable")]
    [DataRow("archive")]
    [DataRow("remove-target")]
    public Task DeviceLifecycleRejectsAlreadyClaimedWork(string change) => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (_, device) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock); await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(policy, token); await DueAsync(network, token);
        var lease = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id && x.Target == "::1");
        Assert.IsTrue(await store.IsCurrentAsync(lease, token));
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            if (change == "archive") current.Archive(clock.Now);
            else if (change == "disable") current.Update(current.Name, current.Address, current.DeepMonitoring, false, clock.Now);
            else
            {
                db.DeviceTargets.Remove(await db.DeviceTargets.SingleAsync(x => x.DeviceId == device.Id, token));
                current.Update(current.Name, current.Address, current.DeepMonitoring, true, clock.Now);
            }
            await db.SaveChangesAsync(token);
        }
        Assert.IsFalse(await store.IsCurrentAsync(lease, token));
        Assert.IsFalse(await store.CommitAsync(lease, policy, new(PingOutcome.Success, "::1", 0), clock.Now, "central", token));
        await using var check = network.CreateDbContext();
        Assert.AreEqual(0, await check.PingSamples.CountAsync(token));
    });

    [TestMethod]
    public Task BadTargetIsRecordedWithoutPreventingOtherTargets() => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Update(current.Name, "never-resolves.invalid", false, true, clock.Now);
            await db.SaveChangesAsync(token);
        }
        var settings = new PingSettings(network, clock); await settings.EnsureAsync(token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(await settings.GetAsync(token), token); await DueAsync(network, token);
        var runner = new PingRunner(settings, store, new MixedProbe(), clock);
        await runner.RunOnceAsync(token);
        await using var check = network.CreateDbContext();
        Assert.AreEqual(3, await check.PingSamples.CountAsync(token));
        Assert.AreEqual(2, await check.PingSamples.CountAsync(x => x.Succeeded, token));
        var failure = await check.PingSamples.SingleAsync(x => x.DeviceId == device.Id, token);
        Assert.AreEqual(PingOutcome.DnsError, failure.Outcome);
        Assert.IsNull(failure.LatencyMilliseconds);
        Assert.AreEqual(Reachability.Unknown, (await check.PingTargets.SingleAsync(x => x.DeviceId == device.Id, token)).State);
    });

    [TestMethod]
    public Task NativeUnspecifiedTargetDoesNotAbortOtherDevices() => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Update(current.Name, "0.0.0.0", false, true, clock.Now);
            await db.SaveChangesAsync(token);
        }
        var settings = new PingSettings(network, clock); await settings.EnsureAsync(token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(await settings.GetAsync(token), token); await DueAsync(network, token);
        await new PingRunner(settings, store, new SystemPingProbe(), clock).RunOnceAsync(token);
        await using var check = network.CreateDbContext();
        Assert.AreEqual(3, await check.PingSamples.CountAsync(token));
        Assert.AreEqual(2, await check.PingSamples.CountAsync(x => x.Succeeded, token));
        Assert.AreEqual(PingOutcome.MonitorError, (await check.PingSamples.SingleAsync(x => x.DeviceId == device.Id, token)).Outcome);
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task StaleGapBreaksReachabilityAndConsecutiveSuccess(bool wasReachable) => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock); await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(policy, token);
        for (var i = 0; i < (wasReachable ? 2 : 1); i++)
        {
            await DueAsync(network, token);
            var lease = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
            Assert.IsTrue(await store.CommitAsync(lease, policy, new(PingOutcome.Success, "127.0.0.1", 1), clock.Now, "central", token));
        }
        clock.Now = clock.Now.AddSeconds(91);
        var stale = await store.GetStatusAsync(device.Id, [device.Address], Convert.ToBase64String(device.Version), token);
        Assert.IsTrue(stale.Targets[0].Stale);
        await DueAsync(network, token);
        var fresh = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        var outcome = wasReachable ? PingOutcome.Timeout : PingOutcome.Success;
        Assert.IsTrue(await store.CommitAsync(fresh, policy, new(outcome, "127.0.0.1", wasReachable ? null : 1), clock.Now, "central", token));
        var status = await store.GetStatusAsync(device.Id, [device.Address], Convert.ToBase64String(device.Version), token);
        Assert.IsFalse(status.Targets[0].Stale);
        Assert.AreEqual(Reachability.Unknown, status.Targets[0].State, "Old evidence must not bridge a stale gap.");
        if (!wasReachable)
        {
            await DueAsync(network, token);
            var next = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
            Assert.IsTrue(await store.CommitAsync(next, policy, new(PingOutcome.Success, "127.0.0.1", 1), clock.Now, "central", token));
            Assert.AreEqual(Reachability.Reachable, (await store.GetStatusAsync(device.Id, [device.Address], Convert.ToBase64String(device.Version), token)).Targets[0].State);
        }
    });

    private sealed class MixedProbe : IPingProbe
    {
        public Task<PingMeasurement> MeasureAsync(string target, int timeoutSeconds, CancellationToken token) =>
            Task.FromResult(target.EndsWith(".invalid", StringComparison.Ordinal)
                ? new PingMeasurement(PingOutcome.DnsError, null, null) : new(PingOutcome.Success, target, 1));
    }

    private Task WithDatabase(Func<Factory<ResourcesDbContext>, Factory<NetworkDbContext>, CancellationToken, Task> test, bool snapshotReads = false) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var resources = new Factory<ResourcesDbContext>(() => new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options));
            var network = new Factory<NetworkDbContext>(() => new NetworkDbContext(new DbContextOptionsBuilder<NetworkDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options));
            await using (var db = resources.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = network.CreateDbContext()) await db.Database.MigrateAsync(token);
            if (snapshotReads)
            {
                var server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString);
                var name = server.InitialCatalog;
                server.InitialCatalog = "master";
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(server.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON";
                await command.ExecuteNonQueryAsync(token);
            }
            await test(resources, network, token);
        }, TestContext.CancellationToken);

    private static async Task<(Device Device, Device Other)> SeedAsync(Factory<ResourcesDbContext> resources, CancellationToken token)
    {
        await using var db = resources.CreateDbContext();
        var device = new Device("Router", "127.0.0.1", false, DateTimeOffset.UtcNow);
        var other = new Device("Server", "127.0.0.2", true, DateTimeOffset.UtcNow);
        db.Devices.AddRange(device, other); db.DeviceTargets.Add(new DeviceTarget(other.Id, "::1"));
        await db.SaveChangesAsync(token);
        return (device, other);
    }
    private static async Task DueAsync(Factory<NetworkDbContext> contexts, CancellationToken token)
    {
        await using var db = contexts.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE network.PingTargets SET DueAt = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
    }
    private sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext { public T CreateDbContext() => create(); }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CountingProbe : IPingProbe
    {
        private int calls;
        public int Calls => calls;
        public Task<PingMeasurement> MeasureAsync(string target, int timeoutSeconds, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new PingMeasurement(PingOutcome.Success, target, 1));
        }
    }
}
