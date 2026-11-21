using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Tests.Fixtures;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HealthRetentionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task CleanupKeepsCurrentEvidenceAndIntervalsCrossingCutoff() =>
        ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var last = now.AddDays(-400);
            var source = new ServiceSource("source", new byte[32], now.AddDays(-500));
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now.AddDays(-500)), now.AddDays(-500));
            var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 3, 2, now.AddDays(-500));
            var network = new HealthNetworkPolicy(["127.0.0.1"], now.AddDays(-500));
            var changed = now.AddDays(-399);
            network.ReplaceNetworks(["127.0.0.1"], changed);
            var schedule = new HealthSchedule(target.Id, true, now);
            schedule.SetEvaluation(0, null, 0, 0, null, last);
            var expired = new HealthStateInterval(target.Id, 1, 1, now.AddDays(-450));
            expired.Close(now.AddDays(-440));
            var crossing = new HealthStateInterval(target.Id, 0, 0, now.AddDays(-370));
            crossing.Close(now.AddDays(-360));
            var open = new HealthStateInterval(target.Id, 0, 0, now.AddDays(-350));
            HealthObservation Observation(long generation, DateTimeOffset at) => new(target.Id, generation, 1,
                new byte[8], new byte[8], null, new byte[8], new byte[8], HealthObservationKind.Healthy, at, at);
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source); setup.Services.Add(service); setup.HealthTargets.Add(target); setup.HealthSchedules.Add(schedule);
                setup.HealthNetworkPolicies.Add(network); setup.HealthNetworkRevisions.Add(new HealthNetworkRevision(network, Guid.CreateVersion7(), changed));
                setup.HealthObservations.AddRange(Observation(1, now.AddDays(-410)), Observation(2, last));
                setup.HealthStateIntervals.AddRange(expired, crossing, open);
                await setup.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var settings = new TelemetryRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider());
            var initial = await settings.GetAsync(token);
            var preview = await settings.PreviewAsync(new(initial.LogDays, initial.TraceDays, initial.Version), Guid.CreateVersion7(), token);
            Assert.IsNotNull(preview);
            Assert.AreEqual(1L, preview.HealthObservations); Assert.AreEqual(1L, preview.HealthIntervals);
            var cleanup = new TelemetryRetentionCleanup(factory);
            Assert.AreEqual((1L, 1L), await cleanup.RunHealthAsync(token));
            Assert.AreEqual((0L, 0L), await cleanup.RunHealthAsync(token));
            await using (var verify = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(last, (await verify.HealthObservations.SingleAsync(token)).ObservedAt);
                var intervals = await verify.HealthStateIntervals.OrderBy(x => x.StartedAt).ToArrayAsync(token);
                Assert.HasCount(2, intervals);
                Assert.AreEqual(crossing.StartedAt, intervals[0].StartedAt);
                Assert.IsNull(intervals[1].EndedAt);
                Assert.AreEqual(changed, await HealthStore.PolicyBoundaryAsync(verify, target.Id, network.Id, network.Generation, last, token));
            }
            var history = await new HealthHistoryReader(factory).ReadAsync(service.Id, now.AddDays(-450), now.AddDays(-440), token);
            Assert.IsNotNull(history);
            Assert.AreEqual(0d, history.Coverage);
            Assert.IsNull(history.Availability);
            Assert.IsTrue(history.Intervals.All(x => x.State == ServiceHealthState.Unknown));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task HistoryRetriesWhenCleanupChangesItsIntervals() =>
        ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = now.AddDays(-400);
            var source = new ServiceSource("source", new byte[32], start);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", start), start);
            var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 3, 2, start);
            var interval = new HealthStateInterval(target.Id, 1, 1, start);
            interval.Close(start.AddMinutes(1));
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                setup.Sources.Add(source); setup.Services.Add(service); setup.HealthTargets.Add(target); setup.HealthStateIntervals.Add(interval);
                await setup.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            await TelemetryRetentionSettings.EnsureAsync(factory, token);
            var barrier = new IntervalReadBarrier();
            var readOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(barrier).Options;
            var reader = new HealthHistoryReader(new PooledDbContextFactory<ServiceObservabilityDbContext>(readOptions));
            var read = reader.ReadAsync(service.Id, start, start.AddMinutes(1), token);
            Exception? failure = null;
            try
            {
                await barrier.Reached.Task.WaitAsync(token);
                Assert.AreEqual((0L, 1L), await new TelemetryRetentionCleanup(factory).RunHealthAsync(token));
                barrier.Release.TrySetResult();
                var result = await read;
                Assert.IsNotNull(result);
                Assert.IsGreaterThanOrEqualTo(2, barrier.Count);
                Assert.AreEqual(0d, result.Coverage);
                Assert.IsTrue(result.Intervals.All(x => x.State == ServiceHealthState.Unknown));
            }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                barrier.Release.TrySetResult();
                try { await read; } catch when (failure is not null) { }
            }
        }, context.CancellationToken);
}
