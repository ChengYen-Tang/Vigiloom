using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InstanceManagementTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Migration_ServiceNamespacePreservesExistingIdentityAndRejectsUnsafeRollback() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = Guid.CreateVersion7();
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261104014140_ServiceCollectionVersion", token);
                // Seed the historical schema explicitly; the current EF model has newer columns.
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Sources] ([Id],[Name],[Verifier],[CreatedAt],[UpdatedAt]) VALUES ({source.Id},{source.Name},{source.Verifier},{now},{now})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Services] ([Id],[SourceId],[ServiceKey],[Environment],[DisplayName],[ApprovedAt],[CreatedAt],[UpdatedAt],[IsPublic]) VALUES ({service},{source.Id},{"api"},{"prod"},{"API"},{now},{now},{now},{false})", token);
                await db.Database.MigrateAsync(token);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var existing = await db.Services.AsNoTracking().SingleAsync(token);
                Assert.AreEqual(service, existing.Id);
                Assert.AreEqual("", existing.ServiceNamespace);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Services] SET [ServiceNamespace] = {" "} WHERE [Id] = {service}", token);
                await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261104014140_ServiceCollectionVersion", token));
            }
            await using var check = new ServiceObservabilityDbContext(options);
            var saved = await check.Services.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(service, saved.Id);
            Assert.AreEqual(" ", saved.ServiceNamespace);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ServiceCollection_StopAndResumeFenceReceptionAndAudit() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            var other = new MonitoredService(new ServiceCandidate(source.Id, "billing", "prod", "Billing", now), now);
            var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 3, 2, now);
            target.SetEnabled(true, now);
            var schedule = new HealthSchedule(target.Id, true, now);
            var evidence = DateTimeOffset.UtcNow.AddSeconds(-2);
            schedule.SetEvaluation(1, evidence, 1, 2, evidence, evidence);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.Add(source); db.Services.AddRange(service, other);
                db.HealthTargets.Add(target); db.HealthSchedules.Add(schedule);
                db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 1, 1, evidence));
                await db.SaveChangesAsync(token);
            }
            var factory = new Factory(options); var actor = Guid.CreateVersion7();
            var admin = new ServiceCollectionAdministration(factory, new OperationAuditWriter());
            var list = new ServiceInstanceAdministration(factory, new OperationAuditWriter());
            var initial = (await list.ListServicesAsync(1, token)).Single(x => x.Id == service.Id);
            var failing = new ServiceCollectionAdministration(factory, new FailAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SetStoppedAsync(service.Id, initial.Version, true, actor, token));
            Assert.IsNull((await list.ListServicesAsync(1, token)).Single(x => x.Id == service.Id).StoppedAt);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual((byte)1, (await db.HealthSchedules.SingleAsync(token)).State);
                Assert.IsNull((await db.HealthStateIntervals.SingleAsync(token)).EndedAt);
            }
            Assert.AreEqual(ServiceCollectionStatus.Updated, await admin.SetStoppedAsync(service.Id, initial.Version, true, actor, token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var intervals = await db.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
                Assert.HasCount(2, intervals);
                Assert.AreEqual(intervals[0].EndedAt, intervals[1].StartedAt);
                Assert.AreEqual((byte)0, (await db.HealthSchedules.SingleAsync(token)).State);
            }
            Assert.AreEqual(ServiceCollectionStatus.Conflict, await admin.SetStoppedAsync(service.Id, initial.Version, false, actor, token));
            Assert.AreEqual(ApprovalStatus.SourceUnavailable, (await new ServiceApproval(factory, new OperationAuditWriter()).ApproveAsync(service.CandidateId!.Value, actor, token)).Status);
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var tx = await db.Database.BeginTransactionAsync(token))
            {
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", token));
                Assert.IsNotNull(await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "billing", "prod", token));
            }
            var stopped = (await list.ListServicesAsync(1, token)).Single(x => x.Id == service.Id);
            Assert.AreEqual(ServiceCollectionStatus.Updated, await admin.SetStoppedAsync(service.Id, stopped.Version, false, actor, token));
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var tx = await db.Database.BeginTransactionAsync(token))
            {
                var binding = await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", token);
                Assert.IsNotNull(binding); Assert.IsGreaterThan(now, binding.ApprovedAt);
            }
            var active = (await list.ListServicesAsync(1, token)).Single(x => x.Id == service.Id);
            Assert.AreEqual(ServiceCollectionStatus.Updated, await admin.SetStoppedAsync(service.Id, active.Version, true, actor, token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var storedSource = await db.Sources.SingleAsync(x => x.Id == source.Id, token);
                storedSource.Revoke(DateTimeOffset.UtcNow); await db.SaveChangesAsync(token);
            }
            stopped = (await list.ListServicesAsync(1, token)).Single(x => x.Id == service.Id);
            Assert.AreEqual(ServiceCollectionStatus.SourceUnavailable, await admin.SetStoppedAsync(service.Id, stopped.Version, false, actor, token));
            var audit = new OperationAuditQueryService(new OperationsFactory(operations));
            Assert.HasCount(3, (await audit.QueryServiceCollectionsAsync(new(now.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), token)).Items);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Migration_AddsVersionsWithoutLosingExistingInstances() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            var id = Guid.CreateVersion7();
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261104010918_ServiceInstances", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Sources] ([Id],[Name],[Verifier],[CreatedAt],[UpdatedAt]) VALUES ({source.Id},{source.Name},{source.Verifier},{now},{now})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Services] ([Id],[SourceId],[ServiceKey],[Environment],[DisplayName],[ApprovedAt],[CreatedAt],[UpdatedAt],[IsPublic]) VALUES ({service.Id},{source.Id},{"orders"},{"prod"},{"Orders"},{now},{now},{now},{false})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({id},{service.Id},{source.Id},{"node"},{now},{now})", token);
                await db.Database.MigrateAsync(token);
            }
            await using var verify = new ServiceObservabilityDbContext(options);
            var existing = await verify.Instances.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(id, existing.Id);
            Assert.AreEqual("node", existing.ExternalInstanceId);
            Assert.HasCount(8, existing.Version);
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ConcurrentRetirementRevocationAndReception_CompleteWithoutDeadlock(bool snapshot) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            Guid instanceId;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                if (snapshot) await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON", token);
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var resolved = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", "node", token);
                instanceId = resolved.InstanceId!.Value;
                await tx.CommitAsync(token);
                var target = new HealthTarget(service.Id, instanceId, "http://127.0.0.1/health", 30, 5, 3, 2, now);
                target.SetEnabled(true, now);
                db.HealthTargets.Add(target); db.HealthSchedules.Add(new HealthSchedule(target.Id, true, now));
                db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 0, 0, now));
                await db.SaveChangesAsync(token);
            }
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var actor = Guid.CreateVersion7();
            var barrier = new BlockingAudit();
            var admin = new ServiceInstanceAdministration(factory, barrier);
            var item = (await admin.ListAsync(service.Id, 1, token)).Single();
            using var concurrency = CancellationTokenSource.CreateLinkedTokenSource(token);
            await using var receiving = new ServiceObservabilityDbContext(options);
            await using var receiveTx = await receiving.Database.BeginTransactionAsync(token);
            Assert.IsNotNull(await ServiceReceptionAuthorization.LockAsync(receiving.Database.GetDbConnection(), receiveTx.GetDbTransaction(), source.Id, "orders", "prod", token));
            var retirement = admin.SetRetiredAsync(instanceId, item.Version, true, actor, concurrency.Token);
            Task? reception = null;
            Task? revoke = null;
            var committed = false;
            Exception? failure = null;
            try
            {
                await barrier.Locked.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                var resolveTask = ServiceInstanceResolver.ResolveAsync(receiving.Database.GetDbConnection(), receiveTx.GetDbTransaction(), source.Id, "orders", "prod", "node", concurrency.Token);
                reception = resolveTask;
                var revokeTask = new SourceAdministration(factory, new OperationAuditWriter()).RevokeAsync(source.Id, actor, concurrency.Token);
                revoke = revokeTask;
                barrier.Release.TrySetResult();
                Assert.AreEqual(InstanceChangeStatus.Updated, await retirement.WaitAsync(TimeSpan.FromSeconds(10), token));
                _ = await resolveTask.WaitAsync(TimeSpan.FromSeconds(10), token);
                await receiveTx.CommitAsync(token);
                committed = true;
                Assert.IsTrue(await revokeTask.WaitAsync(TimeSpan.FromSeconds(10), token));
            }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                barrier.Release.TrySetResult();
                concurrency.Cancel();
                if (reception is not null) try { await reception; } catch (Exception) { /* Preserve the test failure. */ }
                try
                {
                    if (!committed) await receiveTx.RollbackAsync(CancellationToken.None);
                }
                catch (Exception ex) when (failure is not null) { failure.Data["RollbackError"] = ex; }
                finally
                {
                    try { await Task.WhenAll(new[] { retirement, reception, revoke }.OfType<Task>()); }
                    catch (Exception) { /* Operations have been observed; preserve the original failure. */ }
                }
            }
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Management_PolicyAndRetirementAreVersionedAndAuditedAtomically() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Sources.Add(source);
                db.Services.Add(service);
                await db.SaveChangesAsync(token);
            }
            var factory = new Factory(options);
            var actor = Guid.CreateVersion7();
            var failingPolicy = new InstancePolicyAdministration(factory, TimeProvider.System, new FailAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failingPolicy.UpdateAsync(new(2, 5, ""), actor, token));
            var policies = new InstancePolicyAdministration(factory, TimeProvider.System, new OperationAuditWriter());
            var initial = await policies.GetAsync(token);
            Assert.AreEqual(1000, initial.PerService);
            var saved = await policies.UpdateAsync(new(2, 5, initial.Version), actor, token);
            Assert.IsNotNull(saved);
            Assert.IsNull(await policies.UpdateAsync(new(3, 6, initial.Version), actor, token));
            Assert.AreEqual(2, (await policies.GetAsync(token)).PerService);

            Guid instanceId;
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var tx = await db.Database.BeginTransactionAsync(token))
            {
                var resolved = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", "node", token);
                Assert.AreEqual(InstanceResolutionStatus.Resolved, resolved.Status);
                instanceId = resolved.InstanceId!.Value;
                await tx.CommitAsync(token);
            }
            var admin = new ServiceInstanceAdministration(factory, new OperationAuditWriter());
            var item = (await admin.ListAsync(service.Id, 1, token)).Single();
            var failing = new ServiceInstanceAdministration(factory, new FailAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SetRetiredAsync(instanceId, item.Version, true, actor, token));
            Assert.IsNull((await admin.ListAsync(service.Id, 1, token)).Single().RetiredAt);
            Assert.AreEqual(InstanceChangeStatus.Updated, await admin.SetRetiredAsync(instanceId, item.Version, true, actor, token));
            Assert.AreEqual(InstanceChangeStatus.Conflict, await admin.SetRetiredAsync(instanceId, item.Version, false, actor, token));
            var retired = (await admin.ListAsync(service.Id, 1, token)).Single();
            Assert.IsNotNull(retired.RetiredAt);
            Assert.AreEqual(InstanceChangeStatus.Updated, await admin.SetRetiredAsync(instanceId, retired.Version, false, actor, token));
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var tx = await db.Database.BeginTransactionAsync(token))
            {
                var resolved = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", "node", token);
                Assert.AreEqual(instanceId, resolved.InstanceId);
            }
            var audit = new OperationAuditQueryService(new OperationsFactory(operations));
            var query = new OperationAuditQuery(now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.HasCount(1, (await audit.QueryInstancePoliciesAsync(query, token)).Items);
            Assert.HasCount(2, (await audit.QueryServiceInstancesAsync(query, token)).Items);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<ServiceObservabilityDbContext> options) : IDbContextFactory<ServiceObservabilityDbContext>
    {
        public ServiceObservabilityDbContext CreateDbContext() => new(options);
    }
    private sealed class OperationsFactory(DbContextOptions<OperationsDbContext> options) : IDbContextFactory<OperationsDbContext>
    {
        public OperationsDbContext CreateDbContext() => new(options);
    }
    private sealed class FailAudit : IOperationAuditWriter<OperationAudit>
    {
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit value, CancellationToken token)
        {
            await new OperationAuditWriter().AppendAsync(connection, transaction, value, token);
            throw new InvalidOperationException("Injected failure after audit insertion.");
        }
    }
    private sealed class BlockingAudit : IOperationAuditWriter<OperationAudit>
    {
        public TaskCompletionSource Locked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit value, CancellationToken token)
        {
            Locked.TrySetResult();
            await Release.Task.WaitAsync(token);
            await new OperationAuditWriter().AppendAsync(connection, transaction, value, token);
        }
    }
}
