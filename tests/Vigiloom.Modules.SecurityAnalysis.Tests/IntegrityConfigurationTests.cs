using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IntegrityConfigurationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task ScopeLifecycle_PreservesVersionAuditAndBlocksUnsafeDowngrade() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var previous = db.Database.GetMigrations().Single(x => x.EndsWith("_ProcessPartialReasons", StringComparison.Ordinal));
            var device = Guid.CreateVersion7();
            var actor = Guid.CreateVersion7();
            var service = new IntegrityConfiguration(new Factory(options), new Devices(device), TimeProvider.System);
            var input = new IntegrityScopeInput(IntegrityScopeKind.Directory, "/srv/app", true,
                ImmutableArray.Create("logs/**"), 3600, 10000, 1048576, 10485760, 16);
            var oversized = input with { Exclusions = Enumerable.Repeat(new string('界', 512), 32).ToImmutableArray() };
            Assert.IsTrue(oversized.WithIdentity(Guid.CreateVersion7(), 1).IsValid());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.CreateAsync(device, oversized, actor, token));
            var created = await service.CreateAsync(device, input, actor, token);
            Assert.AreEqual(IntegrityConfigurationStatus.Success, created.Status);
            Assert.IsNotNull(created.Scope);
            Assert.AreEqual(1, created.Scope.Scope.Revision);
            Assert.AreEqual(7, created.Scope.Scope.Id.Version);
            var id = created.Scope.Scope.Id;
            var unchanged = await service.UpdateAsync(device, id, input, created.Scope.Version, actor, token);
            Assert.IsNotNull(unchanged.Scope);
            Assert.AreEqual(created.Scope.Version, unchanged.Scope.Version);
            Assert.AreEqual(created.Scope.Scope.Revision, unchanged.Scope.Scope.Revision);
            var updated = await service.UpdateAsync(device, id, input with { Root = "/srv/next" }, created.Scope.Version, actor, token);
            Assert.AreEqual(IntegrityConfigurationStatus.Success, updated.Status);
            Assert.IsNotNull(updated.Scope);
            Assert.AreEqual(2, updated.Scope.Scope.Revision);
            Assert.AreEqual(IntegrityConfigurationStatus.Conflict,
                (await service.UpdateAsync(device, id, input, created.Scope.Version, actor, token)).Status);
            Assert.AreEqual(IntegrityConfigurationStatus.NotFound,
                (await service.UpdateAsync(Guid.CreateVersion7(), id, input, updated.Scope.Version, actor, token)).Status);
            Assert.AreEqual(IntegrityConfigurationStatus.Success,
                (await service.RetireAsync(device, id, updated.Scope.Version, actor, token)).Status);
            Assert.IsEmpty((await service.ListAsync(device, token))!);
            db.ChangeTracker.Clear();
            var audits = await db.IntegrityScopeAudits.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.AreEqual(3, (await db.IntegrityPolicyStates.SingleAsync(token)).Generation);
            Assert.HasCount(3, audits);
            CollectionAssert.AreEqual(new[] { IntegrityScopeAction.Created, IntegrityScopeAction.Updated, IntegrityScopeAction.Retired }, audits.Select(x => x.Action).ToArray());
            Assert.IsTrue(audits.All(x => x.ActorId == actor && x.ScopeId == id));
            await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(previous, token));
            Assert.AreEqual(1, await db.IntegrityScopes.CountAsync(token));
        }, TestContext.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class Devices(Guid deviceId) : IDeviceAgentSource
    {
        public Task<DeviceAgentState?> GetAsync(Guid id, CancellationToken token) =>
            Task.FromResult(id == deviceId ? new DeviceAgentState(id, 1, true) : null);
        public Task<bool> IsCurrentAsync(Guid id, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            Task.FromResult(id == deviceId && generation == 1);
    }
}
