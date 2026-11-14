using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryImportTests(TestContext context)
{
    [TestMethod]
    public Task Migration_RefusesToDiscardWiderSourceLimit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            db.IntelligenceSources.Add(new IntelligenceSource { Key = "github-reviewed", Enabled = true, MaximumBytes = 1_073_741_824, MaximumRecords = 100_000 });
            await db.SaveChangesAsync(token);
            var previous = db.Database.GetMigrations().Single(x => x.EndsWith("_AdvisoryCredentials", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(previous, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(1_073_741_824, await db.IntelligenceSources.AsNoTracking().Select(x => x.MaximumBytes).SingleAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Migration_PreservesPendingProgressOnDowngrade() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var target = db.Database.GetMigrations().Single(x => x.EndsWith("_KevNormalizedRecords", StringComparison.Ordinal));
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(target, token);
            await migrator.MigrateAsync(null, token);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            db.AdvisoryImports.Add(new AdvisoryImport
            {
                SourceId = source.Id, SourceVersion = source.Version, WindowStart = new(2023, 1, 1),
                WindowEnd = new(2023, 2, 1), StartedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(token);
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(target, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(1, await db.AdvisoryImports.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Migration_RejectsDowngradeAfterReconciliationStarts() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            db.AdvisoryImports.Add(new AdvisoryImport
            {
                SourceId = source.Id, SourceVersion = source.Version, WindowStart = new(2023, 1, 1),
                WindowEnd = new(2023, 2, 1), StartedAt = DateTimeOffset.UtcNow, Phase = 3, ReconciliationPhase = 2
            });
            await db.SaveChangesAsync(token);
            var previous = db.Database.GetMigrations().Single(x => x.EndsWith("_AdvisoryCursorRestart", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(previous, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(2, await db.AdvisoryImports.AsNoTracking().Select(x => x.ReconciliationPhase).SingleAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Begin_RejectsWindowCalculatedAgainstChangedBase() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            var a = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "a", RetrievedAt = DateTimeOffset.UtcNow, Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
            var b = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "b", RetrievedAt = DateTimeOffset.UtcNow, Digest = Enumerable.Repeat((byte)1, 32).ToArray(), Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
            byte[] originalVersion;
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                db.IntelligenceRevisions.AddRange(a, b);
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = a.Id, ActivatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(token);
                var active = await db.IntelligenceActivations.SingleAsync(token);
                originalVersion = active.Version.ToArray();
                active.RevisionId = b.Id;
                await db.SaveChangesAsync(token);
            }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            Assert.IsNull(await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token,
                verifyBase: true, expectedBaseId: a.Id, expectedBaseVersion: originalVersion));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(0, await check.AdvisoryImports.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SourceByteLimit_CoversBothPassesWithoutPartialPage(bool exact)
        => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var page = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, new string('a', 1100), null);
            var emptyBytes = Encoding.UTF8.GetBytes("[]");
            var empty = new AdvisoryPage([], null, emptyBytes, new(2023, 1, 1), new(2023, 2, 1), true, null);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true,
                MaximumBytes = page.RawBytes.Length + emptyBytes.Length - (exact ? 0 : 1) };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var import = await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(import);
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, import.Version, null, false, page, token));
            var state = await ReadAsync(contexts, token);
            Assert.AreEqual(exact ? AdvisoryStageResult.Stored : AdvisoryStageResult.LimitExceeded,
                await store.StageAsync(source.Id, import.Id, state.Version, null, true, empty, token));
            state = await ReadAsync(contexts, token);
            if (exact)
            {
                Assert.AreEqual(source.MaximumBytes, state.TotalBytes);
                Assert.AreEqual(AdvisoryStageResult.LimitExceeded, await store.StageAsync(source.Id, import.Id, state.Version, null, false, page, token));
            }
            else Assert.AreEqual(page.RawBytes.Length, state.TotalBytes);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(exact ? 2 : 1, await check.AdvisoryImportPages.CountAsync(token));
            Assert.AreEqual(0, await check.IntelligenceActivations.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task SourceRecordLimit_CountsUniqueRecordsAcrossPages() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true, MaximumRecords = 1 };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var import = await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(import);
            var first = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, "first", "YQ==");
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, import.Version, null, false, first, token));
            var state = await ReadAsync(contexts, token);
            var duplicate = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, "first", "Yg==", requested: "YQ==");
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, state.Version, "YQ==", false, duplicate, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual(1, state.RecordCount);
            Assert.AreEqual(first.RawBytes.Length + duplicate.RawBytes.Length, state.TotalBytes);
            var another = Page("GHSA-efgh-2345-ijkl", "2023-01-04T00:00:00Z", null, "second", null, requested: "Yg==");
            Assert.AreEqual(AdvisoryStageResult.LimitExceeded, await store.StageAsync(source.Id, import.Id, state.Version, "Yg==", false, another, token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(2, await check.AdvisoryImportPages.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task SourceRecordLimit_AlsoAppliesToReconciliation() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true, MaximumRecords = 1 };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var import = await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(import);
            var first = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, "first", null);
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, import.Version, null, false, first, token));
            var state = await ReadAsync(contexts, token);
            var bytes = Encoding.UTF8.GetBytes("[]");
            var empty = new AdvisoryPage([], null, bytes, new(2023, 1, 1), new(2023, 2, 1), true, null);
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, state.Version, null, true, empty, token));
            state = await ReadAsync(contexts, token);
            var repeated = first with { NextCursor = "YQ==" };
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, state.Version, null, false, repeated, token));
            state = await ReadAsync(contexts, token);
            var another = Page("GHSA-efgh-2345-ijkl", "2023-01-04T00:00:00Z", null, "second", null, requested: "YQ==");
            Assert.AreEqual(AdvisoryStageResult.LimitExceeded, await store.StageAsync(source.Id, import.Id, state.Version, "YQ==", false, another, token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(3, await check.AdvisoryImportPages.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task SourcePolicyChange_PreventsOldImportFromCommitting() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var import = await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(import);
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.IntelligenceSources.SingleAsync(token);
                saved.UpdateIntervalSeconds = 43_200;
                await db.SaveChangesAsync(token);
            }
            var page = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, "first", null);
            Assert.AreEqual(AdvisoryStageResult.Conflict, await store.StageAsync(source.Id, import.Id, import.Version, null, false, page, token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(0, await check.AdvisoryImportPages.CountAsync(token));
            Assert.AreEqual(0, await check.AdvisoryImportEntries.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task PagesAndCursorCommitTogether_AndConflictsCannotPublishPartialData() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var import = await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(import);
            Assert.IsNull(await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token));
            var first = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, "first", "Abc=");
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, import.Version, null, false, first, token));
            Assert.AreEqual(AdvisoryStageResult.Conflict, await store.StageAsync(source.Id, import.Id, import.Version, null, false, first, token));
            var state = await ReadAsync(contexts, token);
            Assert.AreEqual("Abc=", state.Cursor);
            Assert.AreEqual(1, state.RecordCount);
            var divergent = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, "different", null, requested: "Abc=");
            Assert.AreEqual(AdvisoryStageResult.Invalid, await store.StageAsync(source.Id, import.Id, state.Version, "Abc=", false, divergent, token));
            await using (var db = contexts.CreateDbContext())
            {
                Assert.AreEqual(1, await db.AdvisoryImportPages.CountAsync(token));
                Assert.AreEqual(1, await db.AdvisoryImportEntries.CountAsync(token));
                Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
            }
            var second = Page("GHSA-efgh-2345-ijkl", "2023-01-04T00:00:00Z", null, "second", "abc=", requested: "Abc=");
            Assert.AreEqual(AdvisoryStageResult.Conflict, await store.StageAsync(source.Id, import.Id, state.Version, "Abc=", false,
                second with { From = new(2022, 1, 1) }, token));
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, state.Version, "Abc=", false, second, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual("abc=", state.Cursor);
            var emptyBytes = Encoding.UTF8.GetBytes("[]");
            var empty = new AdvisoryPage(AdvisoryPageValidator.Validate(emptyBytes), null, emptyBytes, new(2023, 1, 1), new(2023, 2, 1), false, "abc=");
            Assert.AreEqual(AdvisoryStageResult.Invalid, await store.StageAsync(source.Id, import.Id, state.Version, "abc=", false,
                empty with { NextCursor = "Abc=" }, token));
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, state.Version, "abc=", false, empty, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual(2, state.Phase);
            var withdrawn = Page("GHSA-abcd-1234-efgh", "2023-01-05T00:00:00Z", "2023-01-05T00:00:00Z", "withdrawn", null, withdrawnFilter: true);
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, import.Id, state.Version, null, true, withdrawn, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual(3, state.Phase);
            Assert.AreEqual(4, state.PageCount);
            Assert.AreEqual(2, state.RecordCount);
            Assert.IsNull(await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token));
            await using (var db = contexts.CreateDbContext())
            {
                var entry = await db.AdvisoryImportEntries.SingleAsync(x => x.AdvisoryId == "GHSA-abcd-1234-efgh", token);
                Assert.IsNotNull(entry.WithdrawnAt);
                Assert.AreEqual(4, (await db.AdvisoryImportPages.SingleAsync(x => x.RowId == entry.PageRowId, token)).Number);
                Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
            }
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public Task Reconciliation_RequiresMatchingIndependentPass(int variant) =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, TimeProvider.System);
            var state = await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(state);
            var original = Page("GHSA-abcd-1234-efgh", "2023-01-03T00:00:00Z", null, "original", null);
            var emptyBytes = Encoding.UTF8.GetBytes("[]");
            var emptyWithdrawn = new AdvisoryPage([], null, emptyBytes, new(2023, 1, 1), new(2023, 2, 1), true, null);
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, state.Id, state.Version, null, false, original, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, state.Id, state.Version, null, true, emptyWithdrawn, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual(3, state.Phase);
            var check = variant switch
            {
                1 => Page("GHSA-abcd-1234-efgh", "2023-01-04T00:00:00Z", null, "changed", null),
                2 => Page("GHSA-efgh-2345-ijkl", "2023-01-03T00:00:00Z", null, "original", null),
                _ => original
            };
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, state.Id, state.Version, null, false, check, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual(2, state.ReconciliationPhase);
            Assert.AreEqual(AdvisoryStageResult.Stored, await store.StageAsync(source.Id, state.Id, state.Version, null, true, emptyWithdrawn, token));
            state = await ReadAsync(contexts, token);
            Assert.AreEqual(variant == 0 ? 3 : 4, state.ReconciliationPhase);
            Assert.AreEqual(3, state.Phase);
            await using var dbCheck = contexts.CreateDbContext();
            Assert.AreEqual(0, await dbCheck.IntelligenceActivations.CountAsync(token));
        }, context.CancellationToken);

    private static AdvisoryPage Page(string id, string updated, string? withdrawn, string summary, string? next,
        bool withdrawnFilter = false, string? requested = null)
    {
        var withdrawnJson = withdrawn is null ? "null" : $"\"{withdrawn}\"";
        var raw = Encoding.UTF8.GetBytes($$"""
            [{"ghsa_id":"{{id}}","type":"reviewed","published_at":"2023-01-01T00:00:00Z",
              "updated_at":"{{updated}}","withdrawn_at":{{withdrawnJson}},"summary":"{{summary}}","vulnerabilities":[]}]
            """);
        return new(AdvisoryPageValidator.Validate(raw), next, raw, new(2023, 1, 1), new(2023, 2, 1), withdrawnFilter, requested);
    }

    private static async Task<AdvisoryImport> ReadAsync(Factory factory, CancellationToken token)
    {
        await using var db = factory.CreateDbContext();
        return await db.AdvisoryImports.AsNoTracking().SingleAsync(token);
    }

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
}
