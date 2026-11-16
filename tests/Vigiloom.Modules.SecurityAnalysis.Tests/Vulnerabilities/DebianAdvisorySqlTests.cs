using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Net;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DebianAdvisorySqlTests(TestContext context)
{
    [TestMethod]
    public Task Refresh_StagesGoodFeedAndPreservesItAfterInvalidUpdate() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var operations = new OperationsDbContext(operationsOptions)) await operations.Database.MigrateAsync(token);
            using var handler = new FeedHandler { Content = """{"openssl":{"CVE-1":{"releases":{"bookworm":{"status":"open"}}}}}""" };
            var publication = new IntelligencePublication(new TestFactory(options), new OperationAuditWriter(), TimeProvider.System);
            var refresh = new DebianRefresh(new TestFactory(options), new TestHttpFactory(handler), TimeProvider.System, publication);
            Assert.AreEqual(DebianRefreshResult.Disabled, await refresh.RunDueAsync(token));
            var initial = await db.IntelligenceSources.SingleAsync(x => x.Key == "debian-security", token);
            var policy = new IntelligencePolicy(new TestFactory(options), new OperationAuditWriter(), TimeProvider.System);
            var enable = new IntelligencePolicyUpdate(true, initial.UpdateIntervalSeconds, initial.StaleAfterSeconds,
                initial.MaximumBytes, initial.MaximumRecords, Convert.ToBase64String(initial.Version));
            Assert.AreEqual(IntelligencePolicyResult.Updated, await policy.UpdateAsync(initial.Id, enable, Guid.CreateVersion7(), token));
            db.ChangeTracker.Clear();
            Assert.AreEqual(DebianRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual(DebianRefreshResult.NotDue, await refresh.RunDueAsync(token));
            var source = await db.IntelligenceSources.SingleAsync(x => x.Key == "debian-security", token);
            Assert.AreEqual(134217728, source.MaximumBytes);
            Assert.AreEqual(500000, source.MaximumRecords);
            Assert.IsNotNull(source.LastSuccessfulCheckAt);
            var previousSuccess = source.LastSuccessfulCheckAt;
            var revisionId = await db.IntelligenceRevisions.Where(x => x.SourceId == source.Id && x.Normalized).Select(x => x.Id).SingleAsync(token);
            Assert.AreEqual(revisionId, source.LastObservedRevisionId);
            Assert.AreEqual(revisionId, await db.IntelligenceActivations.Where(x => x.SourceId == source.Id).Select(x => x.RevisionId).SingleAsync(token));
            source.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync(token);
            handler.Content = "{}";
            Assert.AreEqual(DebianRefreshResult.Failed, await refresh.RunDueAsync(token));
            db.ChangeTracker.Clear();
            var after = await db.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token);
            Assert.AreEqual(previousSuccess, after.LastSuccessfulCheckAt);
            Assert.IsNotNull(after.LastError);
            Assert.IsTrue(await db.IntelligenceRevisions.AnyAsync(x => x.Id == revisionId && x.Normalized, token));
            Assert.AreEqual(1, await db.DebianAdvisories.CountAsync(x => x.RevisionId == revisionId, token));
        }, context.CancellationToken);

    private sealed class TestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public required string Content { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Content)) });
    }

    [TestMethod]
    public Task RefreshLease_DetectsTerminatedSqlSession() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var sourceId = Guid.CreateVersion7();
            var lease = await IntelligenceRefreshLease.TryAcquireAsync(new TestFactory(options), sourceId, token);
            Assert.IsNotNull(lease);
            try
            {
                Assert.IsTrue(await lease.VerifyHeldAsync(token));
                await using var db = new SecurityAnalysisDbContext(options);
                await db.Database.OpenConnectionAsync(token);
                await using var command = db.Database.GetDbConnection().CreateCommand();
                var session = lease.SessionId;
                Assert.IsGreaterThan(50, session);
                command.CommandText = $"KILL {session}";
                await command.ExecuteNonQueryAsync(token);
                Assert.IsFalse(await lease.VerifyHeldAsync(token));
            }
            finally { await lease.DisposeAsync(); }
            await using var replacement = await IntelligenceRefreshLease.TryAcquireAsync(new TestFactory(options), sourceId, token);
            Assert.IsNotNull(replacement);
            Assert.IsTrue(await replacement.VerifyHeldAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Stage_PublishesOnlyCompleteRevisionAndKeepsFailedStagingUnavailable() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "debian-security", Enabled = true, MaximumBytes = 134217728, MaximumRecords = 500000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            var version = source.Version.ToArray();
            var stage = new DebianRevisionStage(new TestFactory(options), TimeProvider.System);
            var good = Encoding.UTF8.GetBytes("""{"openssl":{"CVE-1":{"releases":{"bookworm":{"status":"resolved","fixed_version":"1.0-1"}}}}}""");
            var result = await stage.StageAsync(source.Id, version, good, "test evidence", token);
            Assert.AreEqual(DebianStageStatus.Staged, result.Status);
            Assert.IsNotNull(result.RevisionId);
            Assert.IsTrue(await db.IntelligenceRevisions.AnyAsync(x => x.Id == result.RevisionId && x.Normalized && x.RecordCount == 1, token));
            Assert.AreEqual(1, await db.DebianAdvisories.CountAsync(x => x.RevisionId == result.RevisionId, token));
            Assert.AreEqual(DebianStageStatus.Unchanged, (await stage.StageAsync(source.Id, version, good, "test evidence", token)).Status);
            Assert.AreEqual(DebianStageStatus.Conflict, (await stage.StageAsync(source.Id, new byte[8], good, "test evidence", token)).Status);
            var entries = Enumerable.Range(0, 256).Select(i => $"\"CVE-{i:D3}\":{{\"releases\":{{\"bookworm\":{{\"status\":\"open\"}}}}}}");
            var bad = Encoding.UTF8.GetBytes("{\"openssl\":{" + string.Join(",", entries) + ",\"CVE-000 \":{\"releases\":{\"bookworm\":{\"status\":\"open\"}}}}}");
            await Assert.ThrowsAsync<DbUpdateException>(() => stage.StageAsync(source.Id, version, bad, "test evidence", token));
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(x => x.SourceId == source.Id && !x.Normalized, token));
            var pendingId = await db.IntelligenceRevisions.Where(x => x.SourceId == source.Id && !x.Normalized).Select(x => x.Id).SingleAsync(token);
            Assert.AreEqual(256, await db.DebianAdvisories.CountAsync(x => x.RevisionId == pendingId, token));
            Assert.AreEqual(1, await db.DebianAdvisories.CountAsync(x => x.RevisionId == result.RevisionId, token));
            await Assert.ThrowsAsync<DbUpdateException>(() => stage.StageAsync(source.Id, version, bad, "test evidence", token));
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(x => x.SourceId == source.Id && !x.Normalized, token));
            var retriedId = await db.IntelligenceRevisions.Where(x => x.SourceId == source.Id && !x.Normalized).Select(x => x.Id).SingleAsync(token);
            Assert.AreNotEqual(pendingId, retriedId);
            Assert.AreEqual(0, await db.DebianAdvisories.CountAsync(x => x.RevisionId == pendingId, token));
            Assert.AreEqual(256, await db.DebianAdvisories.CountAsync(x => x.RevisionId == retriedId, token));
            Assert.AreEqual(DebianStageStatus.Unchanged, (await stage.StageAsync(source.Id, version, good, "test evidence", token)).Status);
            Assert.AreEqual(0, await db.IntelligenceRevisions.CountAsync(x => x.SourceId == source.Id && !x.Normalized, token));
            Assert.AreEqual(0, await db.DebianAdvisories.CountAsync(x => x.RevisionId == retriedId, token));
            var revoked = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "revoked", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = System.Security.Cryptography.SHA256.HashData(good), Artifact = [], SchemaVersion = "old", MappingVersion = "old",
                LicenseEvidence = "test", Revoked = true };
            db.IntelligenceRevisions.Add(revoked);
            await db.SaveChangesAsync(token);
            Assert.AreEqual(DebianStageStatus.Unavailable, (await stage.StageAsync(source.Id, version, good, "test evidence", token)).Status);
        }, context.CancellationToken);

    private sealed class TestFactory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    [TestMethod]
    public Task SourceLimits_AllowRealFeedAndRefuseUnsafeDowngrade() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, index);
            var source = new IntelligenceSource { Key = "debian-security", Enabled = true, MaximumBytes = 134217728, MaximumRecords = 500000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            var migrator = db.GetService<IMigrator>();
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1], token));
            Assert.AreEqual(134217728, await db.IntelligenceSources.Where(x => x.Id == source.Id).Select(x => x.MaximumBytes).SingleAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Migration_IndexesRevisionReleasePackageAndRejectsDuplicateScope() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, index);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[index - 1], token);
            await migrator.MigrateAsync(null, token);
            var source = new IntelligenceSource { Key = "debian-test", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "test", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            DebianAdvisory Record(string release) => new() { RevisionId = revision.Id, Release = release, SourcePackage = "openssl",
                AdvisoryId = "CVE-2026-34182", State = (int)DebianAdvisoryState.FixedVersion, FixedVersion = "3.0.20-1~deb12u2" };
            db.DebianAdvisories.AddRange(Record("bookworm"), Record("trixie"));
            await db.SaveChangesAsync(token);
            Assert.AreEqual(1, await db.DebianAdvisories.CountAsync(x => x.RevisionId == revision.Id && x.Release == "bookworm" && x.SourcePackage == "openssl", token));
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1], token));
            Assert.AreEqual(2, await db.DebianAdvisories.CountAsync(x => x.RevisionId == revision.Id, token));
            db.DebianAdvisories.Add(Record("bookworm"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
        }, context.CancellationToken);
}
