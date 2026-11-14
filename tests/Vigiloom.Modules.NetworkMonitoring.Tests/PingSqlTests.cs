using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using System.Data;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.NetworkMonitoring.Tests.Fixtures;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PingSqlTests
{
    public TestContext TestContext { get; set; } = null!;
    [AssemblyCleanup] public static async Task Cleanup()
    {
        await ReusableNetworkDatabase.DisposeAsync();
        SqlTestDatabase.CompleteRun();
    }

    [TestMethod]
    public Task PingSettingsAuditCommitsAndRollsBackWithSettings() => WithReusableDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var actor = Guid.CreateVersion7();
        var settings = new PingSettings(network, clock, new OperationAuditWriter());
        var initial = await settings.GetAsync(token);
        var input = new PingSettingsInput(20, 2, 2, 2, 8, initial.Version);
        var saved = await settings.UpdateAsync(input, actor, token);
        Assert.IsNotNull(saved);
        await using var networkDb = network.CreateDbContext();
        var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(networkDb.Database.GetConnectionString()).Options;
        var query = new OperationAuditQueryService(new Factory<OperationsDbContext>(() => new OperationsDbContext(options)));
        var range = new OperationAuditQuery(clock.Now.AddDays(-1), clock.Now.AddDays(1));
        var page = await query.QueryPingAsync(range, token);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual(actor, page.Items[0].ActorId);
        Assert.IsNotNull(page.Items[0].PingSettings);
        Assert.AreEqual(initial.IntervalSeconds, page.Items[0].PingSettings!.Previous.IntervalSeconds);
        Assert.AreEqual(20, page.Items[0].PingSettings!.Current.IntervalSeconds);
        Assert.AreEqual(initial.Version, page.Items[0].PingSettings!.PreviousVersion);
        Assert.AreEqual(saved.Version, page.Items[0].PingSettings!.CurrentVersion);
        Assert.IsEmpty((await query.QueryAsync(range, token)).Items);
        Assert.IsNull(await settings.UpdateAsync(input, actor, token));

        await SeedAsync(resources, token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(saved, token);
        var future = clock.Now.AddHours(1);
        await networkDb.PingTargets.ExecuteUpdateAsync(update => update.SetProperty(x => x.DueAt, future), token);
        var failing = new PingSettings(network, clock, new FailingAuditWriter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.UpdateAsync(input with { IntervalSeconds = 30, Version = saved.Version }, actor, token));
        var afterFailure = await settings.GetAsync(token);
        Assert.AreEqual(saved, afterFailure);
        Assert.HasCount(1, (await query.QueryPingAsync(range, token)).Items);
        Assert.IsTrue(await networkDb.PingTargets.AllAsync(x => x.DueAt == future, token));

        var concurrent = await Task.WhenAll(
            settings.UpdateAsync(input with { IntervalSeconds = 40, Version = saved.Version }, actor, token),
            settings.UpdateAsync(input with { IntervalSeconds = 50, Version = saved.Version }, actor, token));
        Assert.AreEqual(1, concurrent.Count(x => x is not null));
        Assert.HasCount(2, (await query.QueryPingAsync(range, token)).Items);

        await networkDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE operations.AuditEvents SET Details = {"{\"Current\":{}}"} WHERE Kind = 2", token);
        var damaged = await query.QueryPingAsync(range, token);
        Assert.HasCount(2, damaged.Items);
        Assert.IsTrue(damaged.Items.All(x => x.PingSettings is null));
    });

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

        var settings = new PingSettings(pooled, TimeProvider.System, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter());
        await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var store = new PingStore(pooled, new DevicePingSource(resources), TimeProvider.System);
        await store.ClaimAsync(policy, token);
        await using (var db = await pooled.CreateDbContextAsync(token))
        {
            Assert.AreSame(original, db);
            Assert.AreEqual(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        await new SpeedSettings(pooled, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter()).GetAsync(token);
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
    public Task LeasesSamplesTransitionsAndStaleness(bool snapshotReads) => WithOptionalSnapshotDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, other) = await SeedAsync(resources, token);
        var source = new DevicePingSource(resources);
        var store = new PingStore(network, source, clock);
        var settings = new PingSettings(network, clock, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter());
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
    public Task FencingDeviceChangesAndSettingsConflicts() => WithReusableDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        var settings = new PingSettings(network, clock, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter());
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
    public Task RunnerSamplesEachConfiguredTargetExactlyOnce() => WithReusableDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter()); await settings.EnsureAsync(token);
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
        Assert.AreEqual(3L, await db.PingRollups.SumAsync(x => x.Attempts, token));
        Assert.IsTrue(await db.PingSamples.AllAsync(x => x.Aggregated, token));
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
    public Task DeviceLifecycleRejectsAlreadyClaimedWork(string change) => WithReusableDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (_, device) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter()); await settings.EnsureAsync(token);
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
    public Task BadTargetIsRecordedWithoutPreventingOtherTargets() => WithReusableDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Update(current.Name, "never-resolves.invalid", false, true, clock.Now);
            await db.SaveChangesAsync(token);
        }
        var settings = new PingSettings(network, clock, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter()); await settings.EnsureAsync(token);
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
        var settings = new PingSettings(network, clock, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter()); await settings.EnsureAsync(token);
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
    public Task StaleGapBreaksReachabilityAndConsecutiveSuccess(bool wasReachable) => WithReusableDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock, new Vigiloom.Modules.AlertingOperations.Application.OperationAuditWriter()); await settings.EnsureAsync(token);
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task RollupBackfill_IsIdempotentAndPreservesLegacyAndOutcomeSemantics(bool snapshotReads) => WithOptionalSnapshotDatabase(async (resources, network, token) =>
    {
        var (device, _) = await SeedAsync(resources, token);
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        await using (var db = network.CreateDbContext())
        {
            db.PingSamples.AddRange(
                new PingSample(device.Id, "central", start.AddMinutes(1), start.AddMinutes(2), true, 10),
                new PingSample(device.Id, "central", start.AddMinutes(2), start.AddMinutes(3), false, null),
                new PingSample(device.Id, "router", null, "central", 1, start.AddMinutes(4), start.AddMinutes(5), PingOutcome.Success, 30),
                new PingSample(device.Id, "router", null, "central", 2, start.AddMinutes(5), start.AddMinutes(6), PingOutcome.Timeout, null),
                new PingSample(device.Id, "router", null, "central", 3, start.AddMinutes(4), start.AddMinutes(7), PingOutcome.DnsError, null),
                new PingSample(device.Id, "extreme", null, "central", 1, start.AddMinutes(1), start.AddMinutes(1), PingOutcome.Success, 1e308),
                new PingSample(device.Id, "extreme", null, "central", 2, start.AddMinutes(2), start.AddMinutes(2), PingOutcome.Success, 1e308));
            await db.SaveChangesAsync(token);
        }
        var backfill = new PingRollupBackfill(network);
        var pending = await new PingHistoryReader(network).QueryAsync(new(device.Id, start, start.AddMinutes(10), "router", "central"), token);
        Assert.IsTrue(pending.RollupsPending);
        Assert.AreEqual(3L, pending.Total);
        await Task.WhenAll(backfill.RunBatchAsync(token), backfill.RunBatchAsync(token));
        Assert.AreEqual(0, await backfill.RunBatchAsync(token));
        await using var check = network.CreateDbContext();
        var rows = await check.PingRollups.AsNoTracking().OrderBy(x => x.WindowStart).ThenBy(x => x.Target).ToArrayAsync(token);
        Assert.HasCount(4, rows);
        var legacy = rows.Single(x => x.Target is null);
        Assert.AreEqual(2L, legacy.Attempts); Assert.AreEqual(1L, legacy.Successes); Assert.AreEqual(1L, legacy.UnknownFailures);
        Assert.AreEqual(10d, legacy.LatencySum);
        var current = rows.Single(x => x.Target == "router" && x.WindowStart == start);
        Assert.AreEqual(2L, current.Attempts); Assert.AreEqual(1L, current.Successes); Assert.AreEqual(1L, current.DnsErrors);
        Assert.AreEqual(30d, current.LatencyMin); Assert.AreEqual(30d, current.LatencyMax);
        Assert.AreEqual(1L, rows.Single(x => x.WindowStart == start.AddMinutes(5)).Timeouts);
        Assert.IsTrue(await check.PingSamples.AllAsync(x => x.Aggregated, token));
        var history = await new PingHistoryReader(network).QueryAsync(new(device.Id, start, start.AddMinutes(10), "router", "central"), token);
        Assert.AreEqual(3L, history.Total);
        Assert.HasCount(2, history.Buckets);
        Assert.AreEqual(30d, history.Buckets[0].AverageMilliseconds);
        Assert.IsNull(history.Buckets[1].AverageMilliseconds);
        Assert.IsFalse(history.RollupsPending);
        Assert.IsTrue(history.Options.Any(x => x.Target is null && x.Location == "central"));
        Assert.IsTrue(history.Options.Any(x => x.Target == "router" && x.Location == "central"));
        var extreme = await new PingHistoryReader(network).QueryAsync(new(device.Id, start, start.AddMinutes(10), "extreme", "central"), token);
        Assert.IsTrue(extreme.Buckets[0].LatencyOverflow);
        Assert.IsNull(extreme.Buckets[0].AverageMilliseconds);
        await check.PingSamples.Where(x => x.DeviceId == device.Id && x.Target == "router").ExecuteDeleteAsync(token);
        var afterRawRemoval = await new PingHistoryReader(network).QueryAsync(new(device.Id, start, start.AddMinutes(10), "router", "central"), token);
        Assert.AreEqual(0L, afterRawRemoval.Total);
        Assert.HasCount(2, afterRawRemoval.Buckets);
        Assert.AreEqual(start.AddMinutes(5), afterRawRemoval.LastObservedAt);
        Assert.IsTrue(afterRawRemoval.Options.Any(x => x.Target == "router"));
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PingSampleAndRollup_RollBackTogether(bool snapshotReads) => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock, new OperationAuditWriter());
        await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(policy, token);
        await DueAsync(network, token);
        var lease = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        await using (var db = network.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [network].[PingRollups] ADD CONSTRAINT [CK_TestRejectPingRollup] CHECK ([Attempts] < 0)", token);
        var failed = await Assert.ThrowsAsync<DbUpdateException>(() => store.CommitAsync(lease, policy, new(PingOutcome.Success, "127.0.0.1", 10), clock.Now, "central", token));
        Assert.IsInstanceOfType<SqlException>(failed.InnerException);
        Assert.AreEqual(547, ((SqlException)failed.InnerException).Number);
        await using (var db = network.CreateDbContext())
        {
            Assert.AreEqual(0, await db.PingSamples.CountAsync(token));
            Assert.AreEqual(0, await db.PingRollups.CountAsync(token));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [network].[PingRollups] DROP CONSTRAINT [CK_TestRejectPingRollup]", token);
        }
        Assert.IsTrue(await store.CommitAsync(lease, policy, new(PingOutcome.Success, "127.0.0.1", 10), clock.Now, "central", token));
        await using var check = network.CreateDbContext();
        Assert.AreEqual(1, await check.PingSamples.CountAsync(token));
        Assert.AreEqual(1L, (await check.PingRollups.SingleAsync(token)).Attempts);
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PingLiveWriteAndBackfill_KeepBothSamples(bool snapshotReads) => WithOptionalSnapshotDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock, new OperationAuditWriter());
        await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var store = new PingStore(network, new DevicePingSource(resources), clock);
        await store.ReconcileAsync(policy, token); await DueAsync(network, token);
        var lease = (await store.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        await using (var db = network.CreateDbContext())
        {
            db.PingSamples.Add(new PingSample(device.Id, lease.Target, "127.0.0.1", "central", lease.Generation + 1000,
                clock.Now, clock.Now, PingOutcome.Success, 10));
            await db.SaveChangesAsync(token);
        }
        var live = store.CommitAsync(lease, policy, new(PingOutcome.Success, "127.0.0.1", 30), clock.Now, "central", token);
        var backfill = new PingRollupBackfill(network).RunBatchAsync(token);
        await Task.WhenAll(live, backfill);
        Assert.IsTrue(await live);
        await using var check = network.CreateDbContext();
        var bucket = await check.PingRollups.SingleAsync(x => x.DeviceId == device.Id, token);
        Assert.AreEqual(2L, bucket.Attempts);
        Assert.AreEqual(2L, bucket.Successes);
        Assert.AreEqual(40d, bucket.LatencySum);
        Assert.IsTrue(await check.PingSamples.Where(x => x.DeviceId == device.Id).AllAsync(x => x.Aggregated, token));
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Commit_RetriesDeadlockWithoutDuplicatingSample(bool disableTarget) => WithDatabase(async (resources, network, token) =>
    {
        var clock = new TestClock();
        var (device, _) = await SeedAsync(resources, token);
        var settings = new PingSettings(network, clock, new OperationAuditWriter());
        await settings.EnsureAsync(token);
        var policy = await settings.GetAsync(token);
        var original = new PingStore(network, new DevicePingSource(resources), clock);
        await original.ReconcileAsync(policy, token); await DueAsync(network, token);
        var lease = (await original.ClaimAsync(policy, token)).Single(x => x.DeviceId == device.Id);
        await using var db = network.CreateDbContext();
        var connectionString = db.Database.GetConnectionString()!;
        await using var other = new SqlConnection(connectionString);
        await other.OpenAsync(token);
        await using var otherTransaction = (SqlTransaction)await other.BeginTransactionAsync(token);
        await using (var hold = other.CreateCommand())
        {
            hold.Transaction = otherTransaction;
            hold.CommandText = "SELECT COUNT(*) FROM [network].[PingRollups] WITH (TABLOCKX, HOLDLOCK)";
            await hold.ExecuteScalarAsync(token);
        }
        var gate = new PauseFirstRollup();
        var options = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(connectionString).AddInterceptors(gate).Options;
        var tested = new PingStore(new PooledDbContextFactory<NetworkDbContext>(options), new DevicePingSource(resources), clock);
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(token);
        var commit = tested.CommitAsync(lease, policy, new(PingOutcome.Success, "127.0.0.1", 5), clock.Now, "central", workers.Token);
        Task? competitor = null;
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            competitor = Task.Run(async () =>
            {
                await using var update = other.CreateCommand();
                update.Transaction = otherTransaction;
                update.CommandText = disableTarget
                    ? "UPDATE [network].[PingTargets] SET [Enabled] = 0 WHERE [DeviceId] = @device AND [Target] = @target"
                    : "UPDATE [network].[PingTargets] SET [DueAt] = [DueAt] WHERE [DeviceId] = @device AND [Target] = @target";
                update.Parameters.AddWithValue("@device", lease.DeviceId);
                update.Parameters.AddWithValue("@target", lease.Target);
                await update.ExecuteNonQueryAsync(workers.Token);
                await otherTransaction.CommitAsync(workers.Token);
            }, workers.Token);
            gate.Release.TrySetResult();
            Assert.AreEqual(!disableTarget, await commit.WaitAsync(TimeSpan.FromSeconds(40), token));
            await competitor;
            Assert.AreEqual(disableTarget ? 1 : 2, gate.Attempts);
            await using var check = network.CreateDbContext();
            Assert.AreEqual(disableTarget ? 0 : 1, await check.PingSamples.CountAsync(x => x.DeviceId == device.Id, token));
            var bucket = await check.PingRollups.SingleOrDefaultAsync(x => x.DeviceId == device.Id, token);
            if (disableTarget) Assert.IsNull(bucket);
            else
            {
                Assert.IsNotNull(bucket);
                Assert.AreEqual(1L, bucket.Attempts);
                Assert.AreEqual(5d, bucket.LatencySum);
                Assert.IsNull((await check.PingTargets.SingleAsync(x => x.DeviceId == device.Id && x.Target == lease.Target, token)).LeaseUntil);
            }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            await workers.CancelAsync();
            gate.Release.TrySetResult();
            try { await commit; } catch when (failure is not null) { }
            if (competitor is not null) { try { await competitor; } catch when (failure is not null) { } }
        }
    });

    private sealed class PauseFirstRollup : DbCommandInterceptor
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command,
            CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[network].[PingRollups] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    command.CommandText = "SET DEADLOCK_PRIORITY LOW; " + command.CommandText;
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
            }
            return result;
        }
    }

    [TestMethod]
    public Task RollupMigration_RejectsDataLosingDowngrade() => WithDatabase(async (resources, network, token) =>
    {
        var (device, _) = await SeedAsync(resources, token);
        var now = DateTimeOffset.UtcNow;
        await using (var db = network.CreateDbContext())
        {
            db.PingSamples.Add(new PingSample(device.Id, "central", now, now, true, 5));
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(1, await new PingRollupBackfill(network).RunBatchAsync(token));
        await using var check = network.CreateDbContext();
        var error = await Assert.ThrowsAsync<SqlException>(() => check.GetService<IMigrator>().MigrateAsync("20261109141451_SpeedRetention", token));
        Assert.AreEqual(51000, error.Number);
        Assert.AreEqual(1, await check.PingSamples.CountAsync(token));
        Assert.AreEqual(1, await check.PingRollups.CountAsync(token));
    });

    [TestMethod]
    public Task PingHistory_PagesByTimeAndKeepsTargetsAndLocationsSeparate() => WithReusableDatabase(async (resources, network, token) =>
    {
        var (device, other) = await SeedAsync(resources, token);
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        await using (var db = network.CreateDbContext())
        {
            for (var i = 0; i < 60; i++)
                db.PingSamples.Add(new PingSample(device.Id, "router", null, "central", i + 1, start, start, PingOutcome.Success, i));
            db.PingSamples.Add(new PingSample(device.Id, "other-target", null, "central", 1, start, start, PingOutcome.Success, 100));
            db.PingSamples.Add(new PingSample(device.Id, "router", null, "other-location", 1000, start, start, PingOutcome.Success, 100));
            db.PingSamples.Add(new PingSample(other.Id, "router", null, "central", 1, start, start, PingOutcome.Success, 100));
            await db.SaveChangesAsync(token);
        }
        var reader = new PingHistoryReader(network);
        var query = new PingHistoryQuery(device.Id, start, start.AddMinutes(5), "router", "central", 1, false);
        var first = await reader.QueryAsync(query, token);
        var second = await reader.QueryAsync(query with { Page = 2 }, token);
        Assert.AreEqual(60L, first.Total);
        Assert.HasCount(50, first.Items); Assert.HasCount(10, second.Items);
        Assert.AreEqual(0d, first.Items[0].LatencyMilliseconds);
        Assert.AreEqual(50d, second.Items[0].LatencyMilliseconds);
        var reverse = await reader.QueryAsync(query with { Descending = true }, token);
        Assert.AreEqual(59d, reverse.Items[0].LatencyMilliseconds);
        Assert.IsTrue(first.RollupsPending);
        Assert.IsFalse(PingHistoryReader.Valid(query with { To = start.AddDays(8) }));
        Assert.IsFalse(PingHistoryReader.Valid(query with { From = start.AddMinutes(1) }));
    });

    private Task WithOptionalSnapshotDatabase(Func<Factory<ResourcesDbContext>, Factory<NetworkDbContext>, CancellationToken, Task> test, bool snapshotReads) =>
        WithReusableDatabase(test, snapshotReads);

    private Task WithReusableDatabase(Func<Factory<ResourcesDbContext>, Factory<NetworkDbContext>, CancellationToken, Task> test, bool snapshotReads = false)
    {
        return snapshotReads
            ? ReusableNetworkDatabase.ExecuteSnapshotAsync(RunAsync, TestContext.CancellationToken)
            : ReusableNetworkDatabase.ExecuteAsync(RunAsync, TestContext.CancellationToken);

        async Task RunAsync(SqlTestDatabase database, CancellationToken token)
        {
            var resources = new Factory<ResourcesDbContext>(() => new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options));
            var network = new Factory<NetworkDbContext>(() => new NetworkDbContext(new DbContextOptionsBuilder<NetworkDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options));
            await test(resources, network, token);
        }
    }

    private Task WithDatabase(Func<Factory<ResourcesDbContext>, Factory<NetworkDbContext>, CancellationToken, Task> test, bool snapshotReads = false) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var resources = new Factory<ResourcesDbContext>(() => new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options));
            var network = new Factory<NetworkDbContext>(() => new NetworkDbContext(new DbContextOptionsBuilder<NetworkDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options));
            if (snapshotReads)
            {
                var server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString);
                var name = server.InitialCatalog;
                server.InitialCatalog = "master";
                server.Pooling = false;
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(server.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON";
                await command.ExecuteNonQueryAsync(token);
            }
            await using (var db = resources.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = network.CreateDbContext()) await db.Database.MigrateAsync(token);
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
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
    private sealed class FailingAuditWriter : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated audit failure.");
    }
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
