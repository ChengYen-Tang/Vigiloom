using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AlertingOperations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AuditRetentionSqlTests(TestContext context)
{
    [AssemblyCleanup] public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PreviewUpdateAndCleanupPreserveRecentAudit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var setup = new OperationsDbContext(options)) await setup.Database.MigrateAsync(token);
            var factory = new PooledDbContextFactory<OperationsDbContext>(options);
            var protection = new EphemeralDataProtectionProvider();
            IAuditRetentionSource[] sources = [];
            var settings = new AuditRetentionSettings(factory, new OperationAuditWriter(), protection, sources);
            var initial = await settings.GetAsync(token);
            Assert.AreEqual(365, initial.Days);
            var now = DateTimeOffset.UtcNow;
            var actor = Guid.CreateVersion7();
            var resource = Guid.CreateVersion7();
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details) VALUES ({actor}, {resource}, {now.AddDays(-40)}, {1}, {"{}"}), ({actor}, {resource}, {now.AddDays(-10)}, {1}, {"{}"})", token);
            }
            var preview = await settings.PreviewAsync(new(30, initial.Version), actor, token);
            Assert.IsNotNull(preview);
            Assert.AreEqual(1L, preview.AffectedRecords);
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(30, initial.Version, null), actor, token));
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(30, initial.Version, preview.ConfirmationToken), Guid.CreateVersion7(), token));
            var saved = await settings.UpdateAsync(new(30, initial.Version, preview.ConfirmationToken), actor, token);
            Assert.IsNotNull(saved);
            Assert.AreEqual(30, saved.Days);
            Assert.IsNull(await settings.UpdateAsync(new(30, initial.Version, preview.ConfirmationToken), actor, token));
            var audit = new OperationAuditQueryService(factory);
            var before = await audit.QueryAuditRetentionAsync(new(now.AddDays(-1), now.AddDays(1)), token);
            Assert.AreEqual(30, before.Items.Single(x => x.AuditRetention is not null).AuditRetention!.CurrentDays);
            Assert.IsEmpty((await audit.QueryAsync(new(now.AddDays(-1), now.AddDays(1)), token)).Items);
            var next = await settings.PreviewAsync(new(20, saved.Version), actor, token);
            Assert.IsNotNull(next);
            var failing = new AuditRetentionSettings(factory, new FailingAuditWriter(), protection, sources);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.UpdateAsync(new(20, saved.Version, next.ConfirmationToken), actor, token));
            Assert.AreEqual(saved, await settings.GetAsync(token));
            Assert.AreEqual(1L, await new AuditRetentionCleanup(factory, settings, sources).RunAsync(token));
            await using var verify = await factory.CreateDbContextAsync(token);
            var count = await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM operations.AuditEvents").SingleAsync(token);
            Assert.AreEqual(2, count);
        }, context.CancellationToken);

    private sealed class FailingAuditWriter : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated audit write failure.");
    }

}
