using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DiscoverySchemaTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Discovery_NamespacesRemainDistinctThroughApprovalAndAuthorization() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var factory = new Factory(options);
            var source = await new SourceCredentials(factory, TimeProvider.System).IssueAsync("source", token);
            var discovery = new CandidateDiscovery(factory);
            foreach (var ns in new[] { "", "A", "A ", "B" })
                Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "api", "prod", "API", token, ns));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "A/api", "prod", "API", token));
            var candidates = await discovery.ListAsync(1, token);
            Assert.HasCount(5, candidates);
            var approval = new ServiceApproval(factory, new OperationAuditWriter());
            var a = candidates.Single(x => x.ServiceNamespace == "A" && x.ServiceKey == "api");
            var approved = await approval.ApproveAsync(a.Id, Guid.CreateVersion7(), token);
            Assert.AreEqual(ApprovalStatus.Approved, approved.Status);
            Assert.AreEqual(CandidateObservation.AlreadyApproved, await discovery.ObserveAsync(source.SourceId, "api", "prod", "API", token, "A"));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "api", "prod", "API", token, "B"));
            await using var dbCheck = new ServiceObservabilityDbContext(options);
            await using var tx = await dbCheck.Database.BeginTransactionAsync(token);
            var accepted = await ServiceReceptionAuthorization.LockAsync(dbCheck.Database.GetDbConnection(), tx.GetDbTransaction(), source.SourceId, "api", "prod", token, "A");
            var rejected = await ServiceReceptionAuthorization.LockAsync(dbCheck.Database.GetDbConnection(), tx.GetDbTransaction(), source.SourceId, "api", "prod", token, "B");
            Assert.AreEqual(approved.ServiceId, accepted!.ServiceId);
            Assert.IsNull(rejected);
        }, context.CancellationToken);

    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        await Fixtures.ReusableServiceDatabase.DisposeAsync();
        SqlTestDatabase.CompleteRun();
    }

    [TestMethod]
    public Task CandidateDiscovery_ContinuedReportsRemainVisibleAfterFirstSeenWindow() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var source = await new SourceCredentials(factory, TimeProvider.System).IssueAsync("source", token);
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            var original = (await discovery.ListAsync(1, token)).Single();
            await using (var db = new ServiceObservabilityDbContext(options))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Candidates] SET [FirstSeen] = DATEADD(minute, -4, SYSUTCDATETIME()), [LastSeen] = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE [Id] = {original.Id}", token);

            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Updated", token));
            var visible = (await discovery.ListAsync(1, token)).Single();
            Assert.AreEqual(original.Id, visible.Id);
            Assert.AreEqual("Updated", visible.DisplayName);
            Assert.IsTrue(visible.LastSeen - visible.FirstSeen > TimeSpan.FromMinutes(3));
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1, await check.Candidates.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Approval_ConcurrentObservationKeepsOneApprovedService(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
            }
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var source = await new SourceCredentials(factory, TimeProvider.System).IssueAsync("source", token);
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            var candidate = (await discovery.ListAsync(1, token)).Single();
            var approval = new ServiceApproval(factory, new OperationAuditWriter());

            var approve = approval.ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
            var observe = discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token);
            await Task.WhenAll(approve, observe);
            Assert.AreEqual(ApprovalStatus.Approved, (await approve).Status);
            Assert.IsTrue((await observe) is CandidateObservation.Recorded or CandidateObservation.AlreadyApproved);
            Assert.AreEqual(CandidateObservation.AlreadyApproved, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            Assert.IsEmpty(await discovery.ListAsync(1, token));
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1, await check.Services.CountAsync(token));
            Assert.AreEqual(0, await check.Candidates.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task CandidateDiscovery_GlobalCapacityIsSerializedAndExpiredCapacityIsReused() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var credentials = new SourceCredentials(factory, TimeProvider.System);
            var sources = new List<SourceCredentialIssue>();
            for (var i = 0; i < 12; i++) sources.Add(await credentials.IssueAsync($"source-{i}", token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                for (var i = 0; i < sources.Count; i++)
                    db.Candidates.AddRange(Enumerable.Range(0, i >= 10 ? 0 : i == 9 ? 999 : 1000)
                        .Select(j => new ServiceCandidate(sources[i].SourceId, $"key-{j}", "prod", "Service", now)));
                await db.SaveChangesAsync(token);
            }
            var discovery = new CandidateDiscovery(factory);
            var outcomes = await Task.WhenAll(
                discovery.ObserveAsync(sources[10].SourceId, "last-a", "prod", "A", token),
                discovery.ObserveAsync(sources[11].SourceId, "last-b", "prod", "B", token));
            Assert.AreEqual(1, outcomes.Count(x => x == CandidateObservation.Recorded));
            Assert.AreEqual(1, outcomes.Count(x => x == CandidateObservation.CapacityReached));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(CandidateDiscovery.GlobalLimit, await db.Candidates.CountAsync(token));
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Candidates] SET [LastSeen] = DATEADD(minute, -4, SYSUTCDATETIME()) WHERE [SourceId] = {sources[0].SourceId} AND [ServiceKey] = {"key-0"}", token);
            }
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(sources[0].SourceId, "replacement", "prod", "Replacement", token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(CandidateDiscovery.GlobalLimit, await db.Candidates.CountAsync(token));
                Assert.IsFalse(await db.Candidates.AnyAsync(x => x.SourceId == sources[0].SourceId && x.ServiceKey == "key-0", token));
                Assert.IsTrue(await db.Candidates.AnyAsync(x => x.SourceId == sources[0].SourceId && x.ServiceKey == "replacement", token));
            }
        }, context.CancellationToken);

    [TestMethod]
    public Task CandidateDiscovery_HidesExpiredAndFutureRowsAndResetsOnReport() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var source = await new SourceCredentials(factory, TimeProvider.System).IssueAsync("source", token);
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "expired", "prod", "Expired", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "future", "prod", "Future", token));
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE [service].[Candidates] SET [LastSeen] = DATEADD(minute, CASE WHEN [ServiceKey] = N'expired' THEN -4 ELSE 4 END, SYSUTCDATETIME())", token);
            }
            Assert.IsEmpty(await discovery.ListAsync(1, token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "future", "prod", "Future", token));
            var visible = (await discovery.ListAsync(1, token)).Single();
            Assert.AreEqual("future", visible.ServiceKey);
            Assert.AreEqual(visible.LastSeen, visible.FirstSeen);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "expired", "prod", "Expired", token));
            var renewed = (await discovery.ListAsync(1, token)).Single(x => x.ServiceKey == "expired");
            Assert.AreEqual(renewed.LastSeen, renewed.FirstSeen);
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ReceptionAuthorization_RequiresApprovedBindingAndActiveSource(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
            }
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var credentials = new SourceCredentials(factory, TimeProvider.System);
            var first = await credentials.IssueAsync("first", token);
            var second = await credentials.IssueAsync("second", token);
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(first.SourceId, "orders", "prod", "Orders", token));
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), first.SourceId, "orders", "prod", token));
            var candidate = (await discovery.ListAsync(1, token)).Single();
            var approved = await new ServiceApproval(factory, new OperationAuditWriter()).ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var connection = db.Database.GetDbConnection();
                var sqlTransaction = transaction.GetDbTransaction();
                await using (var otherDb = new ServiceObservabilityDbContext(options))
                await using (var otherTransaction = await otherDb.Database.BeginTransactionAsync(token))
                    await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceReceptionAuthorization.LockAsync(connection, otherTransaction.GetDbTransaction(), first.SourceId, "orders", "prod", token));
                var binding = await ServiceReceptionAuthorization.LockAsync(connection, sqlTransaction, first.SourceId, "orders", "prod", token);
                Assert.AreEqual(approved.ServiceId, binding!.ServiceId);
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(connection, sqlTransaction, second.SourceId, "orders", "prod", token));
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(connection, sqlTransaction, first.SourceId, "orders", "test", token));
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(connection, sqlTransaction, first.SourceId, "Orders", "prod", token));
                await using var command = connection.CreateCommand();
                command.Transaction = sqlTransaction;
                command.CommandText = "SELECT @@SPID";
                var blockingSession = Convert.ToInt32(await command.ExecuteScalarAsync(token));
                var revocation = new SourceAdministration(factory, new OperationAuditWriter()).RevokeAsync(first.SourceId, Guid.CreateVersion7(), token);
                Exception? failure = null;
                try
                {
                    await using var observer = new ServiceObservabilityDbContext(options);
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    var waiting = false;
                    while (timeout.Elapsed < TimeSpan.FromSeconds(10) && !revocation.IsCompleted)
                    {
                        waiting = await observer.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {blockingSession}").SingleAsync(token) > 0;
                        if (waiting) break;
                        await Task.Delay(50, token);
                    }
                    Assert.IsTrue(waiting, "Revocation must wait for the reception transaction's source lock.");
                }
                catch (Exception ex) { failure = ex; throw; }
                finally
                {
                    try { try { await transaction.CommitAsync(CancellationToken.None); } finally { await revocation; } }
                    catch when (failure is not null) { }
                }
                Assert.IsTrue(await revocation);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), first.SourceId, "orders", "prod", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(second.SourceId, "orders", "prod", "Orders", token));
            var secondCandidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == second.SourceId);
            Assert.AreEqual(ApprovalStatus.Approved, (await new ServiceApproval(factory, new OperationAuditWriter()).ApproveAsync(secondCandidate.Id, Guid.CreateVersion7(), token)).Status);
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var source = await db.Sources.SingleAsync(x => x.Id == second.SourceId, token);
                source.Revoke(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = "SELECT @@SPID";
                var blockingSession = Convert.ToInt32(await command.ExecuteScalarAsync(token));
                async Task<ServiceReceptionBinding?> AuthorizeAsync()
                {
                    await using var reader = new ServiceObservabilityDbContext(options);
                    await using var readTransaction = await reader.Database.BeginTransactionAsync(token);
                    return await ServiceReceptionAuthorization.LockAsync(reader.Database.GetDbConnection(), readTransaction.GetDbTransaction(), second.SourceId, "orders", "prod", token);
                }
                var reception = AuthorizeAsync();
                Exception? failure = null;
                try
                {
                    await using var observer = new ServiceObservabilityDbContext(options);
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    var waiting = false;
                    while (timeout.Elapsed < TimeSpan.FromSeconds(10) && !reception.IsCompleted)
                    {
                        waiting = await observer.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {blockingSession}").SingleAsync(token) > 0;
                        if (waiting) break;
                        await Task.Delay(50, token);
                    }
                    Assert.IsTrue(waiting, "Reception must wait for a source revocation transaction.");
                }
                catch (Exception ex) { failure = ex; throw; }
                finally
                {
                    try { try { await transaction.CommitAsync(CancellationToken.None); } finally { await reception; } }
                    catch when (failure is not null) { }
                }
                Assert.IsNull(await reception);
            }
            var third = await credentials.IssueAsync("third", token);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(third.SourceId, "orders", "prod", "Orders", token));
            var thirdCandidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == third.SourceId);
            var thirdApproval = await new ServiceApproval(factory, new OperationAuditWriter()).ApproveAsync(thirdCandidate.Id, Guid.CreateVersion7(), token);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var service = await db.Services.SingleAsync(x => x.Id == thirdApproval.ServiceId, token);
                service.Revoke(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), third.SourceId, "orders", "prod", token));
        }, context.CancellationToken);

    [TestMethod]
    public Task SourceAdministration_AuditFailureRollsBackIssuanceAndRevocation() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var factory = new Factory(options);
            var actor = Guid.CreateVersion7();
            var failing = new SourceAdministration(factory, new FailingAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.IssueAsync("source", actor, token));
            await using (var check = new ServiceObservabilityDbContext(options)) Assert.AreEqual(0, await check.Sources.CountAsync(token));
            var admin = new SourceAdministration(factory, new OperationAuditWriter());
            var issued = await admin.IssueAsync("source", actor, token);
            var credentials = new SourceCredentials(factory, TimeProvider.System);
            Assert.AreEqual(issued.SourceId, await credentials.AuthenticateAsync(issued.Secret, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RevokeAsync(issued.SourceId, actor, token));
            Assert.AreEqual(issued.SourceId, await credentials.AuthenticateAsync(issued.Secret, token));
            var results = await Task.WhenAll(admin.RevokeAsync(issued.SourceId, actor, token), admin.RevokeAsync(issued.SourceId, actor, token));
            Assert.IsTrue(results.All(x => x));
            Assert.IsNull(await credentials.AuthenticateAsync(issued.Secret, token));
            var query = new OperationAuditQuery(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
            var history = await new OperationAuditQueryService(new OperationsFactory(operations)).QueryServiceSourcesAsync(query, token);
            Assert.HasCount(2, history.Items);
            CollectionAssert.AreEquivalent(new[] { ServiceSourceAction.Issued, ServiceSourceAction.Revoked }, history.Items.Select(x => x.ServiceSource!.Action).ToArray());
            Assert.IsTrue(history.Items.All(x => x.ActorId == actor && x.ResourceId == issued.SourceId));
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SourceRotation_PreservesIdentityRejectsOldVersionAndRollsBackWithAudit(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
            }
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var actor = Guid.CreateVersion7();
            var admin = new SourceAdministration(factory, new OperationAuditWriter());
            var issued = await admin.IssueAsync("source", actor, token);
            var credentials = new SourceCredentials(factory, TimeProvider.System);
            var old = await credentials.AuthenticateVersionedAsync(issued.Secret, token);
            Assert.IsNotNull(old);
            var now = DateTimeOffset.UtcNow;
            var service = new MonitoredService(new ServiceCandidate(issued.SourceId, "orders", "prod", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(options)) { db.Services.Add(service); await db.SaveChangesAsync(token); }
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.AlreadyApproved, await discovery.ObserveAsync(old, "orders", "prod", "Orders", token));
            Guid? instanceId;
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var tx = await db.Database.BeginTransactionAsync(token))
            {
                var instance = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), old.SourceId, "orders", "prod", "node", token);
                Assert.AreEqual(InstanceResolutionStatus.Resolved, instance.Status);
                instanceId = instance.InstanceId;
                await tx.CommitAsync(token);
            }
            // A formally authorized write holds the source lock until its transaction ends;
            // an update like rotation cannot pass it, including with snapshot reads enabled.
            await using (var writer = new ServiceObservabilityDbContext(options))
            await using (var tx = await writer.Database.BeginTransactionAsync(token))
            await using (var contender = new ServiceObservabilityDbContext(options))
            {
                Assert.IsNotNull(await ServiceReceptionAuthorization.LockAsync(writer.Database.GetDbConnection(), tx.GetDbTransaction(), old, "orders", "prod", token));
                await contender.Database.OpenConnectionAsync(token);
                await contender.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 250", token);
                var exception = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
                    contender.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Sources] SET [UpdatedAt] = {now} WHERE [Id] = {old.SourceId}", token));
                Assert.AreEqual(1222, exception.Number);
                await tx.CommitAsync(token);
            }
            var failing = new SourceAdministration(factory, new FailingAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RotateAsync(issued.SourceId, old.Version, actor, token));
            Assert.IsNotNull(await credentials.AuthenticateVersionedAsync(issued.Secret, token));
            var attempts = await Task.WhenAll(admin.RotateAsync(issued.SourceId, old.Version, actor, token), admin.RotateAsync(issued.SourceId, old.Version, actor, token));
            Assert.HasCount(1, attempts.Where(x => x.Status == SourceRotationStatus.Rotated));
            Assert.HasCount(1, attempts.Where(x => x.Status == SourceRotationStatus.Conflict));
            var replacement = attempts.Single(x => x.Status == SourceRotationStatus.Rotated);
            Assert.IsNull(await credentials.AuthenticateVersionedAsync(issued.Secret, token));
            var current = await credentials.AuthenticateVersionedAsync(replacement.Credential, token);
            Assert.IsNotNull(current);
            Assert.AreEqual(old.SourceId, current.SourceId);
            Assert.AreEqual(CandidateObservation.SourceUnavailable, await discovery.ObserveAsync(old, "orders", "prod", "Orders", token));
            Assert.AreEqual(CandidateObservation.AlreadyApproved, await discovery.ObserveAsync(current, "orders", "prod", "Orders", token));
            await using (var db = new ServiceObservabilityDbContext(options))
            await using (var tx = await db.Database.BeginTransactionAsync(token))
            {
                var keys = new[] { new ServiceKey("orders", "prod", "") };
                Assert.IsTrue((await BatchAuthorization.LockAsync(db, tx, old.SourceId, keys, token, old.Version)).SourceUnavailable);
                Assert.IsFalse((await BatchAuthorization.LockAsync(db, tx, current.SourceId, keys, token, current.Version)).SourceUnavailable);
                Assert.IsNull(await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), old, "orders", "prod", token));
                Assert.AreEqual(service.Id, (await ServiceReceptionAuthorization.LockAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), current, "orders", "prod", token))!.ServiceId);
                var instance = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), current.SourceId, "orders", "prod", "node", token);
                Assert.AreEqual(instanceId, instance.InstanceId);
            }
            var query = new OperationAuditQuery(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
            var history = await new OperationAuditQueryService(new OperationsFactory(operations)).QueryServiceSourcesAsync(query, token);
            Assert.HasCount(2, history.Items);
            Assert.IsTrue(history.Items.Any(x => x.ServiceSource!.Action == ServiceSourceAction.CredentialRotated));
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Approval_ConcurrentClicksAreIdempotentAndAuditIsAtomic(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
            }
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var source = await new SourceCredentials(factory, TimeProvider.System).IssueAsync("source", token);
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            var candidate = (await discovery.ListAsync(1, token)).Single();
            var actor = Guid.CreateVersion7();
            var failing = new ServiceApproval(factory, new FailingAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ApproveAsync(candidate.Id, actor, token));
            await using (var check = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(0, await check.Services.CountAsync(token));
                Assert.AreEqual(1, await check.Candidates.CountAsync(token));
            }
            await using (var checkAudit = new OperationsDbContext(operations))
                Assert.AreEqual(0, await checkAudit.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [operations].[AuditEvents] WHERE [Kind] = 4").SingleAsync(token));
            var approval = new ServiceApproval(factory, new OperationAuditWriter());
            var results = await Task.WhenAll(approval.ApproveAsync(candidate.Id, actor, token), approval.ApproveAsync(candidate.Id, actor, token));
            Assert.AreEqual(1, results.Count(x => x.Status == ApprovalStatus.Approved));
            Assert.AreEqual(1, results.Count(x => x.Status == ApprovalStatus.AlreadyApproved));
            Assert.AreEqual(results[0].ServiceId, results[1].ServiceId);
            Assert.AreEqual(CandidateObservation.AlreadyApproved, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            await using var verify = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1, await verify.Services.CountAsync(token));
            Assert.AreEqual(0, await verify.Candidates.CountAsync(token));
            Assert.IsFalse((await verify.Services.SingleAsync(token)).IsPublic);
            await using var audit = new OperationsDbContext(operations);
            Assert.AreEqual(1, await audit.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [operations].[AuditEvents] WHERE [Kind] = 4").SingleAsync(token));
            var query = new OperationAuditQuery(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
            var history = new OperationAuditQueryService(new OperationsFactory(operations));
            var entry = (await history.QueryServiceApprovalsAsync(query, token)).Items.Single();
            Assert.AreEqual(actor, entry.ActorId);
            Assert.AreEqual(results[0].ServiceId, entry.ResourceId);
            Assert.AreEqual(source.SourceId, entry.ServiceApproval!.SourceId);
            Assert.IsEmpty((await history.QueryAsync(query, token)).Items);
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task CandidateDiscovery_LastSourceSlotIsSerializedAndExistingReportsStillUpdate(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
            }
            var factory = new Factory(options);
            var source = await new SourceCredentials(factory, TimeProvider.System).IssueAsync("source", token);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var now = DateTimeOffset.UtcNow;
                db.Candidates.AddRange(Enumerable.Range(0, CandidateDiscovery.PerSourceLimit - 1)
                    .Select(i => new ServiceCandidate(source.SourceId, $"key-{i}", "prod", "Service", now)));
                await db.SaveChangesAsync(token);
            }
            var discovery = new CandidateDiscovery(factory);
            var outcomes = await Task.WhenAll(
                discovery.ObserveAsync(source.SourceId, "final-a", "prod", "A", token),
                discovery.ObserveAsync(source.SourceId, "final-b", "prod", "B", token));
            Assert.AreEqual(1, outcomes.Count(x => x == CandidateObservation.Recorded));
            Assert.AreEqual(1, outcomes.Count(x => x == CandidateObservation.CapacityReached));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "key-0", "prod", "Updated", token));
            await using var verify = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(CandidateDiscovery.PerSourceLimit, await verify.Candidates.CountAsync(token));
            Assert.AreEqual("Updated", (await verify.Candidates.SingleAsync(x => x.SourceId == source.SourceId && x.ServiceKey == "key-0", token)).DisplayName);
        }, context.CancellationToken);

    [TestMethod]
    public Task CandidateDiscovery_UsesSourceIdentityAndRejectsRevokedSources() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var credentials = new SourceCredentials(factory, TimeProvider.System);
            var first = await credentials.IssueAsync("first", token);
            var second = await credentials.IssueAsync("second", token);
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(first.SourceId, "orders", "prod", "Orders", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(first.SourceId, "orders", "prod", "New name", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(second.SourceId, "orders", "prod", "Orders", token));
            var concurrent = await Task.WhenAll(
                discovery.ObserveAsync(second.SourceId, "payments", "prod", "Payments", token),
                discovery.ObserveAsync(second.SourceId, "payments", "prod", "Payments", token));
            Assert.IsTrue(concurrent.All(x => x == CandidateObservation.Recorded));
            var candidates = await discovery.ListAsync(1, token);
            Assert.HasCount(3, candidates);
            Assert.AreEqual("New name", candidates.Single(x => x.SourceId == first.SourceId).DisplayName);
            Assert.IsTrue(await credentials.RevokeAsync(first.SourceId, token));
            Assert.AreEqual(CandidateObservation.SourceUnavailable, await discovery.ObserveAsync(first.SourceId, "new", "prod", "New", token));
            Assert.HasCount(2, await discovery.ListAsync(1, token));
        }, context.CancellationToken);

    [TestMethod]
    public Task SourceCredentials_RejectWrongTypeAndRevokedSecret() =>
        Fixtures.ReusableServiceDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            var credentials = new SourceCredentials(new Factory(options), TimeProvider.System);
            var issued = await credentials.IssueAsync("collector", token);
            Assert.AreEqual(issued.SourceId, await credentials.AuthenticateAsync(issued.Secret, token));
            var altered = issued.Secret[..41] + (issued.Secret[41] == 'A' ? 'B' : 'A') + issued.Secret[42..];
            Assert.IsNull(await credentials.AuthenticateAsync(altered, token));
            Assert.IsNull(await credentials.AuthenticateAsync("vla_" + issued.Secret[4..], token));
            Assert.IsNull(await credentials.AuthenticateAsync(issued.Secret + "=", token));
            Assert.IsNull(await credentials.AuthenticateAsync($"vls_{Guid.CreateVersion7():D}.{issued.Secret[41..]}", token));
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            var finalIndex = alphabet.IndexOf(issued.Secret[^1]);
            var nonCanonical = issued.Secret[..^1] + alphabet[finalIndex + 1];
            Assert.IsNull(await credentials.AuthenticateAsync(nonCanonical, token));
            Assert.IsTrue(await credentials.RevokeAsync(issued.SourceId, token));
            Assert.IsNull(await credentials.AuthenticateAsync(issued.Secret, token));
            Assert.IsFalse(await credentials.RevokeAsync(Guid.CreateVersion7(), token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Migration_SeparatesSourceKeyAndEnvironmentAndRejectsDuplicates() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var first = new ServiceSource("first", new byte[32], now);
            var second = new ServiceSource("second", new byte[32], now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(first, second);
                db.Candidates.AddRange(
                    new ServiceCandidate(first.Id, "orders", "prod", "Orders", now),
                    new ServiceCandidate(first.Id, "Orders", "prod", "Orders", now),
                    new ServiceCandidate(first.Id, "orders", "test", "Orders", now),
                    new ServiceCandidate(second.Id, "orders", "prod", "Orders", now));
                await db.SaveChangesAsync(token);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(4, await db.Candidates.CountAsync(token));
                db.Candidates.Add(new ServiceCandidate(first.Id, " orders ", " prod ", "Duplicate", now));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<ServiceObservabilityDbContext> options) : IDbContextFactory<ServiceObservabilityDbContext>
    {
        public ServiceObservabilityDbContext CreateDbContext() => new(options);
    }

    private sealed class OperationsFactory(DbContextOptions<OperationsDbContext> options) : IDbContextFactory<OperationsDbContext>
    {
        public OperationsDbContext CreateDbContext() => new(options);
    }

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken token)
        {
            await new OperationAuditWriter().AppendAsync(connection, transaction, audit, token);
            throw new InvalidOperationException("Injected failure after audit insertion.");
        }
    }
}
