using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryRevisionBuilderTests(TestContext context)
{
    [TestMethod]
    public Task Activation_RechecksProvenanceAndRecordsAudit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var builder = new AdvisoryRevisionBuilder(contexts, TimeProvider.System);
            var publication = new IntelligencePublication(contexts, new OperationAuditWriter(), TimeProvider.System);
            var first = await StageWindow(store, contexts, source.Id, token, ("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", false));
            var firstResult = await builder.StageAsync(source.Id, first.Id, first.Version, token);
            Assert.IsNotNull(firstResult.RevisionId);
            var actor = Guid.CreateVersion7();
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, firstResult.RevisionId.Value, null, "", false, actor, token));
            var second = await StageWindow(store, contexts, source.Id, token, ("GHSA-bcde-2345-fghi", "2023-01-04T00:00:00Z", false));
            var secondResult = await builder.StageAsync(source.Id, second.Id, second.Version, token);
            Assert.IsNotNull(secondResult.RevisionId);
            var progressStatus = new IntelligenceStatus(contexts, TimeProvider.System);
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceRevisions.SingleAsync(x => x.Id == firstResult.RevisionId, token)).Revoked = true;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual("candidate-unavailable", (await progressStatus.GetAdvisoryProgressAsync(source.Id, token))?.State);
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceRevisions.SingleAsync(x => x.Id == firstResult.RevisionId, token)).Revoked = false;
                await db.SaveChangesAsync(token);
            }
            string version;
            await using (var db = contexts.CreateDbContext()) version = Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, secondResult.RevisionId.Value, firstResult.RevisionId, version, false, actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceRevisions.SingleAsync(x => x.Id == firstResult.RevisionId, token)).Revoked = true;
                await db.SaveChangesAsync(token);
                version = Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
            }
            Assert.AreEqual("active-unavailable", (await progressStatus.GetAdvisoryProgressAsync(source.Id, token))?.State);
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publication.ActivateAsync(source.Id, secondResult.RevisionId.Value, secondResult.RevisionId, version, false, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publication.ActivateAsync(source.Id, firstResult.RevisionId.Value, secondResult.RevisionId, version, false, actor, token));
            await using (var db = contexts.CreateDbContext()) Assert.AreEqual(2, await db.IntelligenceActivationEvents.CountAsync(token));
            var now = DateTimeOffset.UtcNow;
            var audit = await new OperationAuditQueryService(new OperationsFactory(operationsOptions))
                .QueryIntelligenceAsync(new OperationAuditQuery(now.AddMinutes(-10), now.AddMinutes(1)), token);
            Assert.HasCount(2, audit.Items);
            Assert.IsTrue(audit.Items.All(x => x.ActorId == actor && x.ResourceId == source.Id && x.IntelligenceRevision is not null));
            CollectionAssert.AreEquivalent(new[] { firstResult.RevisionId.Value, secondResult.RevisionId.Value },
                audit.Items.Select(x => x.IntelligenceRevision!.RevisionId).ToArray());
        }, context.CancellationToken);

    [TestMethod]
    public Task Migration_RefusesToDiscardStagedRevision() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new Factory(options);
            await using var db = contexts.CreateDbContext();
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            var import = await StageWindow(new AdvisoryImportStore(contexts, TimeProvider.System), contexts, source.Id, token,
                ("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", false));
            var result = await new AdvisoryRevisionBuilder(contexts, TimeProvider.System).StageAsync(source.Id, import.Id, import.Version, token);
            Assert.AreEqual(AdvisoryBuildStatus.Staged, result.Status);
            var target = db.Database.GetMigrations().Single(x => x.EndsWith("_AdvisoryReconciliation", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(target, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(1, await db.AdvisoryRevisionEntries.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Stage_MergesIncrementalRecordsAndRetainsPreviousRevision() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = TimeProvider.System;
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, clock);
            var builder = new AdvisoryRevisionBuilder(contexts, clock);
            var first = await StageWindow(store, contexts, source.Id, token,
                ("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", false),
                ("GHSA-bcde-2345-fghi", "2023-01-03T00:00:00Z", false),
                ("GHSA-defg-4567-hijk", "2023-01-03T00:00:00Z", false));
            var firstResult = await builder.StageAsync(source.Id, first.Id, first.Version, token);
            Assert.AreEqual(AdvisoryBuildStatus.Staged, firstResult.Status);
            Assert.IsNotNull(firstResult.RevisionId);
            var replay = await builder.StageAsync(source.Id, first.Id, first.Version, token);
            Assert.AreEqual(AdvisoryBuildStatus.AlreadyStaged, replay.Status);
            Assert.AreEqual(firstResult.RevisionId, replay.RevisionId);
            await using (var db = contexts.CreateDbContext())
            {
                Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = firstResult.RevisionId.Value, ActivatedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(token);
            }
            var second = await StageWindow(store, contexts, source.Id, token,
                ("GHSA-abcd-1234-efgh", "2023-01-02T00:00:00Z", false),
                ("GHSA-bcde-2345-fghi", "2023-01-04T00:00:00Z", true),
                ("GHSA-cdef-3456-ghij", "2023-01-05T00:00:00Z", false));
            var secondResult = await builder.StageAsync(source.Id, second.Id, second.Version, token);
            Assert.AreEqual(AdvisoryBuildStatus.Staged, secondResult.Status);
            Assert.IsNotNull(secondResult.RevisionId);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(firstResult.RevisionId, (await check.IntelligenceActivations.SingleAsync(token)).RevisionId);
            Assert.AreEqual(3, await check.AdvisoryRevisionEntries.CountAsync(x => x.RevisionId == firstResult.RevisionId, token));
            Assert.AreEqual(4, await check.AdvisoryRevisionEntries.CountAsync(x => x.RevisionId == secondResult.RevisionId, token));
            var current = await (from member in check.AdvisoryRevisionEntries
                join entry in check.AdvisoryImportEntries on member.EntryRowId equals entry.RowId
                where member.RevisionId == secondResult.RevisionId
                select new { member.AdvisoryId, entry.ImportId, entry.WithdrawnAt }).ToListAsync(token);
            Assert.AreEqual(first.Id, current.Single(x => x.AdvisoryId == "GHSA-abcd-1234-efgh").ImportId);
            Assert.AreEqual(first.Id, current.Single(x => x.AdvisoryId == "GHSA-defg-4567-hijk").ImportId);
            Assert.AreEqual(second.Id, current.Single(x => x.AdvisoryId == "GHSA-bcde-2345-fghi").ImportId);
            Assert.IsNotNull(current.Single(x => x.AdvisoryId == "GHSA-bcde-2345-fghi").WithdrawnAt);
            Assert.AreEqual(second.Id, current.Single(x => x.AdvisoryId == "GHSA-cdef-3456-ghij").ImportId);
            Assert.IsNull((await check.IntelligenceRevisions.SingleAsync(x => x.Id == secondResult.RevisionId, token)).ReleasedAt);
            (await check.IntelligenceActivations.SingleAsync(token)).RevisionId = secondResult.RevisionId.Value;
            (await check.IntelligenceRevisions.SingleAsync(x => x.Id == firstResult.RevisionId, token)).Revoked = true;
            await check.SaveChangesAsync(token);
            var third = await StageWindow(store, contexts, source.Id, token);
            var denied = await builder.StageAsync(source.Id, third.Id, third.Version, token);
            Assert.AreEqual(AdvisoryBuildStatus.InvalidBase, denied.Status);
            Assert.AreEqual(2, await check.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Stage_OverLimitRollsBackCandidateAndKeepsImportRetryable() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true, MaximumRecords = 1 };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var initial = await StageWindow(store, contexts, source.Id, token, ("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", false));
            var builder = new AdvisoryRevisionBuilder(contexts, TimeProvider.System);
            var initialResult = await builder.StageAsync(source.Id, initial.Id, initial.Version, token);
            Assert.IsNotNull(initialResult.RevisionId);
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = initialResult.RevisionId.Value, ActivatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(token);
            }
            var import = await StageWindow(store, contexts, source.Id, token, ("GHSA-bcde-2345-fghi", "2023-01-03T00:00:00Z", false));
            var result = await builder.StageAsync(source.Id, import.Id, import.Version, token);
            Assert.AreEqual(AdvisoryBuildStatus.LimitExceeded, result.Status);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(1, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(1, await check.AdvisoryRevisionEntries.CountAsync(token));
            Assert.AreEqual(3, (await check.AdvisoryImports.SingleAsync(x => x.Id == import.Id, token)).Phase);
            Assert.AreEqual(initialResult.RevisionId, (await check.IntelligenceActivations.SingleAsync(token)).RevisionId);
        }, context.CancellationToken);

    [TestMethod]
    public Task Stage_RejectsSameTimestampWithDifferentContent() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var builder = new AdvisoryRevisionBuilder(contexts, TimeProvider.System);
            var initial = await StageWindow(store, contexts, source.Id, token, ("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", false));
            var initialResult = await builder.StageAsync(source.Id, initial.Id, initial.Version, token);
            Assert.IsNotNull(initialResult.RevisionId);
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = initialResult.RevisionId.Value, ActivatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(token);
            }
            var changed = await StageWindow(store, contexts, source.Id, token, ("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", true));
            var result = await builder.StageAsync(source.Id, changed.Id, changed.Version, token);
            Assert.AreEqual(AdvisoryBuildStatus.Conflict, result.Status);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(1, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(initialResult.RevisionId, (await check.IntelligenceActivations.SingleAsync(token)).RevisionId);
        }, context.CancellationToken);

    private static async Task<AdvisoryImport> StageWindow(AdvisoryImportStore store, Factory contexts, Guid sourceId,
        CancellationToken token, params (string Id, string Updated, bool Withdrawn)[] records)
    {
        var import = await store.BeginAsync(sourceId, new(2023, 1, 1), new(2023, 2, 1), token);
        Assert.IsNotNull(import);
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var withdrawn in new[] { false, true })
            {
                var selected = records.Where(x => x.Withdrawn == withdrawn).Select(x =>
                    $$"""{"ghsa_id":"{{x.Id}}","type":"reviewed","published_at":"2023-01-01T00:00:00Z","updated_at":"{{x.Updated}}","withdrawn_at":{{(x.Withdrawn ? $"\"{x.Updated}\"" : "null")}},"vulnerabilities":[]}""");
                var bytes = Encoding.UTF8.GetBytes($"[{string.Join(',', selected)}]");
                var page = new AdvisoryPage(AdvisoryPageValidator.Validate(bytes), null, bytes,
                    new(2023, 1, 1), new(2023, 2, 1), withdrawn, null);
                Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(sourceId, import.Id, import.Version, null, withdrawn, page, token));
                await using var db = contexts.CreateDbContext();
                import = await db.AdvisoryImports.AsNoTracking().SingleAsync(x => x.Id == import.Id, token);
            }
        }
        Assert.AreEqual(3, import.ReconciliationPhase);
        return import;
    }

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class OperationsFactory(DbContextOptions<OperationsDbContext> options) : IDbContextFactory<OperationsDbContext>
    {
        public OperationsDbContext CreateDbContext() => new(options);
    }
}
