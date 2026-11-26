using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Migrator.Security;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SecurityMigrationTests(TestContext context)
{
    [TestMethod]
    public Task CopiesAllSecurityTablesWithAgentAndSourceIdentity() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outer) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(source.ConnectionString, null, token);
            await SecurityDatabaseCommand.RunAsync(source.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 2);
            Guid sourceId;
            Guid revisionId;
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                var mapped = db.Model.GetEntityTypes().Select(x => x.GetTableName()).Where(x => x is not null).Distinct().Order().ToArray();
                CollectionAssert.AreEqual(mapped, SecurityDatabaseCommand.Tables.Order().ToArray());
                var feed = new IntelligenceSource { Key = "migration-test", Enabled = true };
                sourceId = feed.Id;
                db.IntelligenceSources.Add(feed);
                var revision = new IntelligenceRevision { SourceId = sourceId, UpstreamVersion = "1", RetrievedAt = DateTimeOffset.UtcNow,
                    Digest = new byte[32], Artifact = [1], SchemaVersion = "1", MappingVersion = "1", LicenseEvidence = "test" };
                revisionId = revision.Id;
                db.IntelligenceRevisions.Add(revision);
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = sourceId, RevisionId = revisionId, ActivatedAt = DateTimeOffset.UtcNow });
                db.InventorySources.Add(new InventorySourceState(owner, InventorySource.HostSystem));
                await db.SaveChangesAsync(token);
            }
            await SecurityDatabaseCommand.RunAsync(target.ConnectionString, source.ConnectionString, token);
            var targetOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(target.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using (var db = new SecurityAnalysisDbContext(targetOptions))
            {
                Assert.IsTrue(await db.IntelligenceSources.AnyAsync(x => x.Id == sourceId && x.Key == "migration-test", token));
                Assert.IsTrue(await db.IntelligenceActivations.AnyAsync(x => x.SourceId == sourceId && x.RevisionId == revisionId, token));
                Assert.IsTrue(await db.InventorySources.AnyAsync(x => x.AgentId == owner.AgentId && x.DeviceId == owner.DeviceId && x.DeviceGeneration == 2, token));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => SecurityDatabaseCommand.RunAsync(target.ConnectionString, source.ConnectionString, token));
        }, outer), context.CancellationToken);
}
