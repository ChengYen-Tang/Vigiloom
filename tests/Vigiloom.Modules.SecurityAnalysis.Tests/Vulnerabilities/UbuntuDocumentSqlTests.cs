using Microsoft.Data.SqlClient;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class UbuntuDocumentSqlTests(TestContext context)
{
    [TestMethod]
    public Task Publication_BlocksRollbackAcrossWithdrawalButAllowsLaterReappearance() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = UbuntuRevisionStage.SourceKey, Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            var old = NewRevision(source.Id, 1);
            var withdrawn = NewRevision(source.Id, 2);
            var reappeared = NewRevision(source.Id, 3);
            var future = NewRevision(source.Id, 4);
            foreach (var revision in new[] { old, withdrawn, reappeared, future })
            {
                revision.MappingVersion = UbuntuRevisionStage.MappingVersion;
                revision.SchemaVersion = UbuntuRevisionStage.SchemaVersion;
                revision.LicenseEvidence = UbuntuRevisionStage.LicenseEvidence;
            }
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.AddRange(old, withdrawn, reappeared, future);
            await db.SaveChangesAsync(token);
            var factory = new Factory(options);
            await StageDocument(old, "2025-01-01T00:00:00Z", null);
            await StageDocument(withdrawn, "2025-02-01T00:00:00Z", "2025-02-02T00:00:00Z");
            await StageDocument(reappeared, "2025-03-01T00:00:00Z", null);
            await StageDocument(future, "2099-03-01T00:00:00Z", null);
            var publication = new IntelligencePublication(factory, new OperationAuditWriter(), TimeProvider.System);
            Assert.AreEqual(IntelligencePublicationResult.Published,
                await publication.ActivateScheduledAsync(source.Id, withdrawn.Id, null, "", token));
            var activation = await db.IntelligenceActivations.AsNoTracking().SingleAsync(token);
            var version = Convert.ToBase64String(activation.Version);
            Assert.AreEqual(IntelligencePublicationResult.Unavailable,
                await publication.ActivateScheduledAsync(source.Id, old.Id, withdrawn.Id, version, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable,
                await publication.ActivateScheduledAsync(source.Id, future.Id, withdrawn.Id, version, token));
            Assert.AreEqual(IntelligencePublicationResult.Published,
                await publication.ActivateScheduledAsync(source.Id, reappeared.Id, withdrawn.Id, version, token));

            async Task StageDocument(IntelligenceRevision revision, string modified, string? withdrawnAt)
            {
                var extra = withdrawnAt is null ? "" : $",\"withdrawn\":\"{withdrawnAt}\"";
                var raw = Encoding.UTF8.GetBytes($"{{\"id\":\"UBUNTU-CVE-2025-0012\",\"modified\":\"{modified}\"{extra},\"affected\":[{{\"package\":{{\"ecosystem\":\"Ubuntu:24.04:LTS\",\"name\":\"openssl\"}}}}]}}");
                var record = UbuntuOsvReader.Read(raw, 1024);
                var document = await UbuntuOsvDocument.CreateAsync(record, raw, token);
                var writer = await UbuntuRevisionWriter.OpenAsync(factory, revision.Id, revision.MappingVersion, token);
                await writer.WriteAsync([new(record.Id, document)], token);
                await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Normalized, true), token);
            }
        }, context.CancellationToken);

    [TestMethod]
    public Task Candidates_RequireEligibleRevisionAndReportTruncation() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = UbuntuRevisionStage.SourceKey, Enabled = true };
            var revision = NewRevision(source.Id, 3);
            revision.MappingVersion = UbuntuRevisionStage.MappingVersion;
            revision.SchemaVersion = UbuntuRevisionStage.SchemaVersion;
            revision.LicenseEvidence = UbuntuRevisionStage.LicenseEvidence;
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            var writer = await UbuntuRevisionWriter.OpenAsync(new Factory(options), revision.Id, revision.MappingVersion, token);
            foreach (var suffix in new[] { "0012", "0013" })
            {
                var json = $$$"""
                    {"id":"UBUNTU-CVE-2025-{{{suffix}}}","modified":"2025-01-01T00:00:00Z","affected":[{"package":{"ecosystem":"Ubuntu:24.04:LTS","name":"openssl"}}]}
                    """;
                var raw = Encoding.UTF8.GetBytes(json);
                var record = UbuntuOsvReader.Read(raw, 1024);
                var document = await UbuntuOsvDocument.CreateAsync(record, raw, token);
                await writer.WriteAsync([new(record.Id, document)], token);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => UbuntuCandidates.LoadAsync(db, source.Id, revision.Id, "Ubuntu:24.04:LTS", "openssl", 1, null, token));
            await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Normalized, true), token);
            var limited = await UbuntuCandidates.LoadAsync(db, source.Id, revision.Id, "Ubuntu:24.04:LTS", "openssl", 1, null, token);
            Assert.IsFalse(limited.Complete);
            Assert.HasCount(1, limited.Records);
            var next = await UbuntuCandidates.LoadAsync(db, source.Id, revision.Id, "Ubuntu:24.04:LTS", "openssl", 1, limited.NextAfterAdvisoryId, token);
            Assert.IsTrue(next.Complete);
            Assert.AreNotEqual(limited.Records[0].Id, next.Records.Single().Id);
            var complete = await UbuntuCandidates.LoadAsync(db, source.Id, revision.Id, "Ubuntu:24.04:LTS", "openssl", 10, null, token);
            Assert.IsTrue(complete.Complete);
            Assert.HasCount(2, complete.Records);
            var noMatch = await UbuntuCandidates.LoadAsync(db, source.Id, revision.Id, "Ubuntu:22.04", "openssl", 10, null, token);
            Assert.IsTrue(noMatch.Complete);
            Assert.HasCount(0, noMatch.Records);
            var otherBytes = await db.UbuntuDocuments.AsNoTracking().Where(x => x.AdvisoryId.EndsWith("0013"))
                .Select(x => x.Compressed).SingleAsync(token);
            await db.UbuntuDocuments.Where(x => x.AdvisoryId.EndsWith("0012"))
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.Compressed, otherBytes), token);
            await Assert.ThrowsAsync<InvalidDataException>(() => UbuntuCandidates.LoadAsync(db, source.Id, revision.Id, "Ubuntu:24.04:LTS", "openssl", 10, null, token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_StagesOfficialEndpointAndKeepsFailedUpdateUnpublished() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            using var handler = new FeedHandler { Content = Convert.FromBase64String(UbuntuOsvArchiveTests.Valid) };
            var refresh = new UbuntuRefresh(new Factory(options), new HttpFactory(handler), TimeProvider.System, new IntelligencePublication(new Factory(options), new OperationAuditWriter(), TimeProvider.System));
            Assert.AreEqual(UbuntuRefreshResult.Disabled, await refresh.RunDueAsync(token));
            var source = await db.IntelligenceSources.SingleAsync(x => x.Key == UbuntuRevisionStage.SourceKey, token);
            Assert.AreEqual(134217728, source.MaximumBytes);
            source.Enabled = true;
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            Assert.AreEqual(UbuntuRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual("https://security-metadata.canonical.com/osv/osv-all.tar.xz", handler.RequestedUri?.AbsoluteUri);
            source = await db.IntelligenceSources.SingleAsync(x => x.Key == UbuntuRevisionStage.SourceKey, token);
            Assert.IsNotNull(source.LastSuccessfulCheckAt);
            Assert.AreEqual((await db.IntelligenceRevisions.SingleAsync(x => x.Normalized, token)).Id, source.LastObservedRevisionId);
            var observed = source.LastObservedRevisionId;
            source.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            Assert.AreEqual(UbuntuRefreshResult.Unchanged, await refresh.RunDueAsync(token));
            source = await db.IntelligenceSources.SingleAsync(x => x.Key == UbuntuRevisionStage.SourceKey, token);
            Assert.AreEqual(observed, source.LastObservedRevisionId);
            source.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            handler.Content = [1, 2, 3];
            Assert.AreEqual(UbuntuRefreshResult.Failed, await refresh.RunDueAsync(token));
            db.ChangeTracker.Clear();
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(x => x.Normalized, token));
            Assert.AreEqual(observed, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            var afterFailure = await db.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token);
            Assert.IsNotNull(afterFailure.LastError);
            Assert.AreEqual(observed, afterFailure.LastObservedRevisionId);
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_RecordsDatabaseWriteFailureAndBacksOff() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            db.IntelligenceSources.Add(new IntelligenceSource { Key = UbuntuRevisionStage.SourceKey, Enabled = true, MaximumBytes = 134217728, MaximumRecords = 150000 });
            await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [security].[UbuntuTestFailure] ON [security].[UbuntuDocuments] AFTER INSERT AS THROW 51000, 'test insert failure', 1;", token);
            using var handler = new FeedHandler { Content = Convert.FromBase64String(UbuntuOsvArchiveTests.Valid) };
            var refresh = new UbuntuRefresh(new Factory(options), new HttpFactory(handler), TimeProvider.System, new IntelligencePublication(new Factory(options), new OperationAuditWriter(), TimeProvider.System));

            Assert.AreEqual(UbuntuRefreshResult.Failed, await refresh.RunDueAsync(token));
            db.ChangeTracker.Clear();
            var source = await db.IntelligenceSources.SingleAsync(x => x.Key == UbuntuRevisionStage.SourceKey, token);
            Assert.IsNotNull(source.LastError);
            Assert.IsNotNull(source.LastCheckedAt);
            Assert.IsNull(source.LastSuccessfulCheckAt);
            Assert.IsNull(source.LastObservedRevisionId);
            Assert.AreEqual(UbuntuRefreshResult.NotDue, await refresh.RunDueAsync(token));

            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [security].[UbuntuTestFailure]", token);
            source.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            Assert.AreEqual(UbuntuRefreshResult.Staged, await refresh.RunDueAsync(token));
            source = await db.IntelligenceSources.SingleAsync(x => x.Key == UbuntuRevisionStage.SourceKey, token);
            Assert.IsNull(source.LastError);
            Assert.IsNotNull(source.LastSuccessfulCheckAt);
            Assert.IsNotNull(source.LastObservedRevisionId);
        }, context.CancellationToken);

    [TestMethod]
    public Task RevisionStage_RejectsDamagedArchiveThenRetriesAndReusesRevision() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = UbuntuRevisionStage.SourceKey, Enabled = true, MaximumRecords = 100 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            var factory = new Factory(options);
            await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(factory, source.Id, token);
            Assert.IsNotNull(lease);
            var stage = new UbuntuRevisionStage(factory, TimeProvider.System);
            var valid = Convert.FromBase64String(UbuntuOsvArchiveTests.Valid);
            await Assert.ThrowsAsync<InvalidDataException>(() => stage.StageWithLeaseAsync(source.Id, source.Version, [.. valid, 1], lease, token));
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(x => !x.Normalized, token));
            var staged = await stage.StageWithLeaseAsync(source.Id, source.Version, valid, lease, token);
            Assert.AreEqual(UbuntuStageStatus.Staged, staged.Status);
            var revision = await db.IntelligenceRevisions.SingleAsync(token);
            Assert.IsTrue(revision.Normalized);
            Assert.AreEqual(1, revision.RecordCount);
            Assert.AreEqual(1, await db.UbuntuRevisionDocuments.CountAsync(token));
            Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
            var abandoned = NewRevision(source.Id, 2);
            abandoned.MappingVersion = UbuntuRevisionStage.MappingVersion;
            db.IntelligenceRevisions.Add(abandoned);
            await db.SaveChangesAsync(token);
            var existing = await db.UbuntuDocuments.AsNoTracking().SingleAsync(token);
            var writer = await UbuntuRevisionWriter.OpenAsync(factory, abandoned.Id, UbuntuRevisionStage.MappingVersion, token);
            var shared = new UbuntuOsvDocument(existing.Digest, existing.Compressed, existing.OriginalLength, []);
            var orphan = new UbuntuOsvDocument(Enumerable.Repeat((byte)99, 32).ToArray(), [1], 1,
                [new UbuntuPackageScope("Ubuntu:24.04:LTS", "orphan")]);
            await writer.WriteAsync([new(existing.AdvisoryId, shared), new("UBUNTU-CVE-2025-9999", orphan)], token);
            Assert.AreEqual(2, await db.UbuntuDocuments.CountAsync(token));
            var repeated = await stage.StageWithLeaseAsync(source.Id, source.Version, valid, lease, token);
            Assert.AreEqual(UbuntuStageStatus.Unchanged, repeated.Status);
            Assert.AreEqual(staged.RevisionId, repeated.RevisionId);
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(1, await db.UbuntuDocuments.CountAsync(token));
            Assert.AreEqual(1, await db.UbuntuRevisionDocuments.CountAsync(token));
            Assert.IsFalse(await db.UbuntuScopes.AnyAsync(x => x.SourcePackage == "orphan", token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Archive_StagesActualDocumentsWithoutPublishingRevision() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "ubuntu-archive-test" };
            var revision = NewRevision(source.Id, 1);
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            var writer = await UbuntuRevisionWriter.OpenAsync(new Factory(options), revision.Id, "ubuntu-v1", token);
            using var archive = new MemoryStream(Convert.FromBase64String(UbuntuOsvArchiveTests.Valid));
            var count = await UbuntuArchiveStaging.StageAsync(archive, writer, 20_000, 10, 1024, 10, token);
            Assert.AreEqual(1, count);
            Assert.AreEqual("UBUNTU-CVE-2025-0012", (await db.UbuntuRevisionDocuments.SingleAsync(token)).AdvisoryId);
            db.ChangeTracker.Clear();
            Assert.IsFalse((await db.IntelligenceRevisions.SingleAsync(x => x.Id == revision.Id, token)).Normalized);
        }, context.CancellationToken);

    [TestMethod]
    public Task Writer_RollsBackIncompleteDocumentAndReusesCommittedDocument() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "ubuntu-writer-test" };
            var first = NewRevision(source.Id, 1);
            var second = NewRevision(source.Id, 2);
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.AddRange(first, second);
            await db.SaveChangesAsync(token);
            var factory = new Factory(options);
            var writer = await UbuntuRevisionWriter.OpenAsync(factory, first.Id, "ubuntu-v1", token);
            var digest = Enumerable.Repeat((byte)7, 32).ToArray();
            var scope = new UbuntuPackageScope("Ubuntu:24.04:LTS", "openssl");
            var bad = new UbuntuOsvDocument(digest, [1], 1, [scope, scope]);
            await Assert.ThrowsAsync<DbUpdateException>(() => writer.WriteAsync([new("UBUNTU-CVE-2025-0012", bad)], token));
            Assert.AreEqual(0, await db.UbuntuDocuments.CountAsync(token));
            Assert.AreEqual(0, await db.UbuntuRevisionDocuments.CountAsync(token));
            var good = bad with { Scopes = [scope] };
            var oversized = good with { Compressed = new byte[UbuntuRevisionWriter.MaximumBatchBytes + 1] };
            await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync([new("UBUNTU-CVE-2025-0012", oversized)], token));
            Assert.AreEqual(0, await db.UbuntuDocuments.CountAsync(token));
            await writer.WriteAsync([new("UBUNTU-CVE-2025-0012", good)], token);
            var next = await UbuntuRevisionWriter.OpenAsync(factory, second.Id, "ubuntu-v1", token);
            await next.WriteAsync([new("UBUNTU-CVE-2025-0012", good)], token);
            Assert.AreEqual(1, await db.UbuntuDocuments.CountAsync(token));
            Assert.AreEqual(1, await db.UbuntuScopes.CountAsync(token));
            Assert.AreEqual(2, await db.UbuntuRevisionDocuments.CountAsync(token));
            await db.IntelligenceRevisions.Where(x => x.Id == second.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Normalized, true), token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => next.WriteAsync([new("UBUNTU-CVE-2025-0013", good)], token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Index_ReusesDocumentsAcrossRevisionsAndProtectsRetainedEvidence() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "ubuntu-test" };
            var first = NewRevision(source.Id, 1);
            var second = NewRevision(source.Id, 2);
            var digest = new byte[32];
            digest[0] = 42;
            const string mapping = "ubuntu-v1";
            const string advisory = "UBUNTU-CVE-2025-0012";
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.AddRange(first, second);
            db.UbuntuDocuments.Add(new UbuntuDocument { Digest = digest, MappingVersion = mapping, AdvisoryId = advisory, Compressed = [1], OriginalLength = 1 });
            db.UbuntuScopes.Add(new UbuntuScope { Digest = digest, MappingVersion = mapping, Ecosystem = "Ubuntu:24.04:LTS", SourcePackage = "openssl" });
            db.UbuntuRevisionDocuments.AddRange(
                new UbuntuRevisionDocument { RevisionId = first.Id, Digest = digest, MappingVersion = mapping, AdvisoryId = advisory },
                new UbuntuRevisionDocument { RevisionId = second.Id, Digest = digest, MappingVersion = mapping, AdvisoryId = advisory });
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            Assert.AreEqual(1, await db.UbuntuDocuments.CountAsync(token));
            Assert.AreEqual(2, await db.UbuntuRevisionDocuments.CountAsync(token));
            var matches = await (from scope in db.UbuntuScopes
                join member in db.UbuntuRevisionDocuments on new { scope.Digest, scope.MappingVersion } equals new { member.Digest, member.MappingVersion }
                where member.RevisionId == second.Id && scope.Ecosystem == "Ubuntu:24.04:LTS" && scope.SourcePackage == "openssl"
                select member.AdvisoryId).ToListAsync(token);
            CollectionAssert.AreEqual(new[] { advisory }, matches);

            db.UbuntuRevisionDocuments.Add(new UbuntuRevisionDocument { RevisionId = first.Id, Digest = digest, MappingVersion = mapping, AdvisoryId = "wrong" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.ChangeTracker.Clear();
            db.UbuntuScopes.Add(new UbuntuScope { Digest = digest, MappingVersion = mapping, Ecosystem = "Ubuntu:24.04:LTS", SourcePackage = "openssl" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.ChangeTracker.Clear();
            db.UbuntuScopes.Add(new UbuntuScope { Digest = new byte[32], MappingVersion = mapping, Ecosystem = "Ubuntu:24.04:LTS", SourcePackage = "missing" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.ChangeTracker.Clear();
            var migrations = db.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, index);
            var migrator = db.GetService<IMigrator>();
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1], token));
            Assert.AreEqual(2, await db.UbuntuRevisionDocuments.CountAsync(token));
            await db.UbuntuRevisionDocuments.ExecuteDeleteAsync(token);
            await db.UbuntuScopes.ExecuteDeleteAsync(token);
            await db.UbuntuDocuments.ExecuteDeleteAsync(token);
            await migrator.MigrateAsync(migrations[index - 1], token);
            await migrator.MigrateAsync(null, token);
            Assert.AreEqual(0, await db.UbuntuDocuments.CountAsync(token));
        }, context.CancellationToken);

    private static IntelligenceRevision NewRevision(Guid sourceId, byte value) => new()
    {
        SourceId = sourceId, UpstreamVersion = value.ToString(), RetrievedAt = DateTimeOffset.UtcNow,
        Digest = Enumerable.Repeat(value, 32).ToArray(), Artifact = [value], SchemaVersion = "osv-v1",
        MappingVersion = "ubuntu-v1", LicenseEvidence = "CC BY-SA 4.0", RecordCount = 1
    };

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public required byte[] Content { get; set; }
        public Uri? RequestedUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
        }
    }
}
