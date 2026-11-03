using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DiscoverySchemaTests(TestContext context)
{
    [AssemblyCleanup]
    public static void Cleanup() => SqlTestDatabase.CompleteRun();

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
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
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
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
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
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
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
