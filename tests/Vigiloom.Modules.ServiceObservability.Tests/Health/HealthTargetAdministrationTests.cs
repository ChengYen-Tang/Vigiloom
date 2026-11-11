using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Tests.Fixtures;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HealthTargetAdministrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Save_UsesServiceAuthorizationVersionAndAtomicAudit() =>
        ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var serviceOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operationOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(serviceOptions))
            {
                db.Sources.Add(source);
                db.Services.Add(service);
                await db.SaveChangesAsync(token);
            }
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(serviceOptions);
            var administration = new HealthTargetAdministration(factory, TimeProvider.System, new OperationAuditWriter());
            var actor = Guid.CreateVersion7();
            var input = new HealthTargetInput("https://example.test/health?tag=ready", 30, 5, 3, 2, true, "");
            var created = await administration.CreateAsync(new(service.Id, null, input), actor, token);
            Assert.AreEqual(HealthTargetStatus.Saved, created.Status);
            Assert.IsNotNull(created.Target);
            var oldVersion = created.Target.Version;
            var updated = await administration.UpdateAsync(created.Target.Id, input with { IntervalSeconds = 60, Version = oldVersion }, actor, token);
            Assert.AreEqual(HealthTargetStatus.Saved, updated.Status);
            var conflict = await administration.UpdateAsync(created.Target.Id, input with { Version = oldVersion }, actor, token);
            Assert.AreEqual(HealthTargetStatus.Conflict, conflict.Status);

            var failing = new HealthTargetAdministration(factory, TimeProvider.System, new FailingAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.UpdateAsync(created.Target.Id,
                input with { IntervalSeconds = 120, Version = updated.Target!.Version }, actor, token));
            Assert.AreEqual(60, (await administration.GetAsync(created.Target.Id, token))!.IntervalSeconds);
            var audit = new OperationAuditQueryService(new PooledDbContextFactory<OperationsDbContext>(operationOptions));
            var events = await audit.QueryHealthTargetsAsync(new(now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.HasCount(2, events.Items);
            Assert.IsTrue(events.Items.All(x => x.HealthTarget?.ServiceId == service.Id));
            var change = events.Items.Single(x => x.HealthTarget?.Action == HealthTargetAction.Updated).HealthTarget!;
            Assert.AreEqual(30, change.Previous!.IntervalSeconds);
            Assert.AreEqual(60, change.Current.IntervalSeconds);
            Assert.AreEqual(oldVersion, change.PreviousVersion);
            Assert.AreEqual(updated.Target!.Version, change.CurrentVersion);
            Assert.IsFalse(change.EndpointChanged);
            var disabled = await administration.UpdateAsync(created.Target.Id,
                input with { IntervalSeconds = 60, Enabled = false, Version = updated.Target.Version }, actor, token);
            Assert.AreEqual(HealthTargetStatus.Saved, disabled.Status);
            await using (var db = new ServiceObservabilityDbContext(serviceOptions))
                Assert.AreEqual((byte)4, (await db.HealthStateIntervals.SingleAsync(x => x.TargetId == created.Target.Id && x.EndedAt == null, token)).State);
            var enabled = await administration.UpdateAsync(created.Target.Id,
                input with { IntervalSeconds = 60, Enabled = true, Version = disabled.Target!.Version }, actor, token);
            Assert.AreEqual(HealthTargetStatus.Saved, enabled.Status);
            await using (var db = new ServiceObservabilityDbContext(serviceOptions))
                Assert.AreEqual((byte)0, (await db.HealthStateIntervals.SingleAsync(x => x.TargetId == created.Target.Id && x.EndedAt == null, token)).State);
            await using (var db = new ServiceObservabilityDbContext(serviceOptions))
            {
                (await db.Sources.SingleAsync(token)).Revoke(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            var unavailable = await administration.UpdateAsync(created.Target.Id,
                input with { Version = updated.Target!.Version }, actor, token);
            Assert.AreEqual(HealthTargetStatus.Unavailable, unavailable.Status);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Update_AfterPolicyChange_PreservesUnknownGap() =>
        ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var observed = DateTimeOffset.UtcNow.AddSeconds(-100);
            var changed = observed.AddSeconds(10);
            var source = new ServiceSource("source", new byte[32], observed);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", observed), observed);
            var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 3, 2, observed);
            target.SetEnabled(true, observed);
            var policy = new HealthNetworkPolicy(["127.0.0.0/8"], observed);
            var schedule = new HealthSchedule(target.Id, true, observed);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.Add(source); db.Services.Add(service); db.HealthTargets.Add(target);
                db.HealthSchedules.Add(schedule); db.HealthNetworkPolicies.Add(policy);
                db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 1, 1, observed));
                await db.SaveChangesAsync(token);
                schedule.SetEvaluation(1, observed, 1, 2, observed, observed);
                schedule.SetVersions(source.Version, service.Version, null, target.Version, policy.Version);
                db.HealthObservations.Add(new HealthObservation(target.Id, 1, policy.Generation, source.Version,
                    service.Version, null, target.Version, policy.Version, HealthObservationKind.Healthy, observed, observed));
                policy.ReplaceNetworks([], changed);
                db.HealthNetworkRevisions.Add(new HealthNetworkRevision(policy, Guid.CreateVersion7(), changed));
                await db.SaveChangesAsync(token);
            }
            var administration = new HealthTargetAdministration(new PooledDbContextFactory<ServiceObservabilityDbContext>(options),
                TimeProvider.System, new OperationAuditWriter());
            var result = await administration.UpdateAsync(target.Id, new HealthTargetInput(target.Endpoint, 60, 5, 3, 2, true,
                Convert.ToBase64String(target.Version)), Guid.CreateVersion7(), token);
            Assert.AreEqual(HealthTargetStatus.Saved, result.Status);
            await using var verify = new ServiceObservabilityDbContext(options);
            var intervals = await verify.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(3, intervals);
            Assert.AreEqual(changed, intervals[0].EndedAt);
            Assert.AreEqual(changed, intervals[1].StartedAt);
            Assert.AreEqual(intervals[1].EndedAt, intervals[2].StartedAt);
            Assert.AreEqual((byte)0, intervals[1].State);
            Assert.AreEqual((byte)0, intervals[2].State);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ConcurrentCreates_CannotExceedPerServiceLimit() =>
        ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var serviceOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operationOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(serviceOptions))
            {
                db.Sources.Add(source); db.Services.Add(service);
                db.HealthTargets.AddRange(Enumerable.Range(0, HealthTargetAdministration.MaxTargetsPerService - 1)
                    .Select(i => new HealthTarget(service.Id, null, $"https://example.test/{i}", 30, 5, 3, 2, now)));
                await db.SaveChangesAsync(token);
            }
            var admin = new HealthTargetAdministration(new PooledDbContextFactory<ServiceObservabilityDbContext>(serviceOptions),
                TimeProvider.System, new OperationAuditWriter());
            var input = new HealthTargetInput("https://example.test/health", 30, 5, 3, 2, true, "");
            var actor = Guid.CreateVersion7();
            var results = await Task.WhenAll(admin.CreateAsync(new(service.Id, null, input), actor, token),
                admin.CreateAsync(new(service.Id, null, input), actor, token));
            Assert.AreEqual(1, results.Count(x => x.Status == HealthTargetStatus.Saved));
            Assert.AreEqual(1, results.Count(x => x.Status == HealthTargetStatus.LimitReached));
            await using var check = new ServiceObservabilityDbContext(serviceOptions);
            Assert.AreEqual(HealthTargetAdministration.MaxTargetsPerService, await check.HealthTargets.CountAsync(token));
        }, context.CancellationToken);

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Simulated audit failure"));
    }
}
