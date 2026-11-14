using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IntelligencePublicationTests(TestContext context)
{
    [TestMethod]
    public Task Selection_AndAuditAreAtomic_AndReductionNeedsConfirmation() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var securityOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory<SecurityAnalysisDbContext>(() => new SecurityAnalysisDbContext(securityOptions));
            var operations = new Factory<OperationsDbContext>(() => new OperationsDbContext(operationsOptions));
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = operations.CreateDbContext()) await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            var first = Revision(source.Id, 100, 1);
            var reduced = Revision(source.Id, 80, 2);
            var revoked = Revision(source.Id, 90, 3);
            revoked.Revoked = true;
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                db.IntelligenceRevisions.AddRange(first, reduced, revoked);
                await db.SaveChangesAsync(token);
            }
            var actor = Guid.CreateVersion7();
            var status = new IntelligenceStatus(contexts, TimeProvider.System);
            var initial = (await status.GetSourcesAsync(1, token)).Items.Single();
            Assert.IsNull(initial.ActiveRevisionId);
            Assert.AreEqual("", initial.ActivationVersion);
            var publication = new IntelligencePublication(contexts, new OperationAuditWriter(), TimeProvider.System);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, first.Id, null, "", false, actor, token));
            var firstVersion = await VersionAsync(contexts, token);
            Assert.AreEqual(IntelligencePublicationResult.Unchanged, await publication.ActivateAsync(source.Id, first.Id, first.Id, firstVersion, false, actor, token));
            Assert.AreEqual(firstVersion, await VersionAsync(contexts, token));
            Assert.AreEqual(IntelligencePublicationResult.Conflict, await publication.ActivateAsync(source.Id, reduced.Id, null, "", false, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.ConfirmationRequired, await publication.ActivateAsync(source.Id, reduced.Id, first.Id, firstVersion, false, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.ConfirmationRequired, await publication.ActivateScheduledAsync(source.Id, reduced.Id, first.Id, firstVersion, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publication.ActivateAsync(source.Id, revoked.Id, first.Id, firstVersion, true, actor, token));
            var failing = new IntelligencePublication(contexts, new FailingAudit(), TimeProvider.System);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ActivateAsync(source.Id, reduced.Id, first.Id, firstVersion, true, actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                Assert.AreEqual(first.Id, (await db.IntelligenceActivations.SingleAsync(token)).RevisionId);
                Assert.AreEqual(1, await db.IntelligenceActivationEvents.CountAsync(token));
            }
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, reduced.Id, first.Id, firstVersion, true, actor, token));
            var reducedVersion = await VersionAsync(contexts, token);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, first.Id, reduced.Id, reducedVersion, false, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Conflict, await publication.ActivateAsync(source.Id, reduced.Id, first.Id, firstVersion, true, actor, token));
            var now = DateTimeOffset.UtcNow;
            var page = await new OperationAuditQueryService(operations).QueryIntelligenceAsync(new OperationAuditQuery(now.AddMinutes(-10), now.AddMinutes(1)), token);
            Assert.HasCount(3, page.Items);
            Assert.IsTrue(page.Items.Any(x => x.IntelligenceRevision?.LargeReductionConfirmed == true));
            var policy = new IntelligencePolicy(contexts, new OperationAuditWriter(), TimeProvider.System);
            var currentSource = (await status.GetSourcesAsync(1, token)).Items.Single();
            var change = new IntelligencePolicyUpdate(true, 43_200, 259_200, 16 * 1024 * 1024, 50_000, currentSource.Version);
            Assert.AreEqual(IntelligencePolicyResult.Updated, await policy.UpdateAsync(source.Id, change, actor, token));
            Assert.AreEqual(IntelligencePolicyResult.Conflict, await policy.UpdateAsync(source.Id, change, actor, token));
            var afterPolicy = (await status.GetSourcesAsync(1, token)).Items.Single();
            Assert.AreEqual(43_200, afterPolicy.UpdateIntervalSeconds);
            var invalidAudit = new IntelligencePolicy(contexts, new FailingAudit(), TimeProvider.System);
            await Assert.ThrowsAsync<InvalidOperationException>(() => invalidAudit.UpdateAsync(source.Id, change with { Version = afterPolicy.Version, Enabled = false }, actor, token));
            Assert.IsTrue((await status.GetSourcesAsync(1, token)).Items.Single().Enabled);
            var policyAudit = await new OperationAuditQueryService(operations).QueryIntelligenceAsync(new OperationAuditQuery(now.AddMinutes(-10), now.AddMinutes(1)), token);
            Assert.IsTrue(policyAudit.Items.Any(x => x.IntelligencePolicy?.Current.UpdateIntervalSeconds == 43_200));
            var revisions = await status.GetRevisionsAsync(source.Id, 1, token);
            Assert.IsNotNull(revisions);
            Assert.HasCount(3, revisions.Items);
            var currentVersion = await VersionAsync(contexts, token);
            var concurrent = await Task.WhenAll(
                publication.ActivateAsync(source.Id, reduced.Id, first.Id, currentVersion, true, actor, token),
                publication.ActivateAsync(source.Id, reduced.Id, first.Id, currentVersion, true, actor, token));
            CollectionAssert.AreEquivalent(new[] { IntelligencePublicationResult.Published, IntelligencePublicationResult.Conflict }, concurrent);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateScheduledAsync(source.Id, first.Id,
                reduced.Id, await VersionAsync(contexts, token), token));
            await using (var db = contexts.CreateDbContext())
            {
                var scheduled = await db.IntelligenceActivationEvents.SingleAsync(x => x.Scheduled, token);
                Assert.IsNull(scheduled.ActorId);
                Assert.AreEqual(actor, (await db.IntelligenceActivationEvents.FirstAsync(x => !x.Scheduled, token)).ActorId);
                var latest = await db.IntelligenceSources.SingleAsync(token);
                latest.LastSuccessfulCheckAt = DateTimeOffset.UtcNow.AddDays(-4);
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publication.ActivateAsync(source.Id, reduced.Id, first.Id,
                await VersionAsync(contexts, token), true, actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                var target = db.Database.GetMigrations().Single(x => x.EndsWith("_IntelligenceSources", StringComparison.Ordinal));
                var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(target, token));
                Assert.AreEqual(51000, error.Number);
                Assert.IsGreaterThan(0, await db.IntelligenceActivationEvents.CountAsync(token));
            }
        }, context.CancellationToken);

    private static IntelligenceRevision Revision(Guid sourceId, int count, byte digest) => new()
    {
        SourceId = sourceId, UpstreamVersion = $"v{digest}", ReleasedAt = DateTimeOffset.UtcNow,
        RetrievedAt = DateTimeOffset.UtcNow, Digest = Enumerable.Repeat(digest, 32).ToArray(), Artifact = [digest],
        SchemaVersion = KevCatalogValidator.SchemaRevision, MappingVersion = KevUpdate.MappingVersion,
        LicenseEvidence = KevUpdate.LicenseEvidence, RecordCount = count, Normalized = true
    };

    private static async Task<string> VersionAsync(Factory<SecurityAnalysisDbContext> contexts, CancellationToken token)
    {
        await using var db = contexts.CreateDbContext();
        return Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
    }

    private sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext
    {
        public T CreateDbContext() => create();
    }

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Injected audit failure");
    }
}
