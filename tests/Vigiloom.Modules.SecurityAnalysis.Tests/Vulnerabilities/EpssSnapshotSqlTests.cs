using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
public sealed class EpssSnapshotSqlTests(TestContext context)
{
    [TestMethod]
    public Task Refresh_ActivatesValidatedScoresAndKeepsThemAfterFailedDownload() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var content = $"#model_version:v1,score_date:{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\ncve,epss,percentile\n" +
                string.Concat(Enumerable.Range(0, 1001).Select(i => $"CVE-2025-{i + 1000},0.00006,0.2\n"));
            using var handler = new Handler { Body = Compress(content) };
            var factory = new Factory(options);
            var refresh = new EpssRefresh(factory, new HttpFactory(handler), TimeProvider.System,
                new IntelligencePublication(factory, new OperationAuditWriter(), TimeProvider.System));
            Assert.AreEqual(EpssRefreshResult.Disabled, await refresh.RunDueAsync(token));
            var source = await db.IntelligenceSources.SingleAsync(token);
            source.Enabled = true;
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            Assert.AreEqual(EpssRefreshResult.Staged, await refresh.RunDueAsync(token));
            var active = (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId;
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.LastCheckedAt, DateTimeOffset.UtcNow.AddDays(-2)), token);
            handler.Body = [1, 2, 3];
            Assert.AreEqual(EpssRefreshResult.Failed, await refresh.RunDueAsync(token));
            Assert.AreEqual(active, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
        }, context.CancellationToken);

    [TestMethod]
    public Task Staging_RejectsDuplicatesTruncationAndRevokedDigests() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = EpssSnapshotStage.SourceKey, Enabled = true, MaximumRecords = 10000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            var content = $"#model_version:v1,score_date:{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\ncve,epss,percentile\n" +
                string.Concat(Enumerable.Range(0, 1001).Select(i => $"CVE-2025-{i + 1000},0.00006,0.2\n"));
            var good = Compress(content);
            using var handler = new Handler { Body = good };
            using var http = new HttpClient(handler);
            var stage = new EpssSnapshotStage(new Factory(options), TimeProvider.System);
            var first = await stage.StageAsync(source.Id, source.Version, new EpssClient(http), EpssRefresh.LicenseEvidence, token);
            Assert.AreEqual(EpssStageStatus.Staged, first.Status);
            Assert.AreEqual(1001, await db.EpssScores.CountAsync(x => x.RevisionId == first.RevisionId, token));
            Assert.IsTrue(await EpssRevisionEligibility.IsPublishableAsync(db, source.Id, first.RevisionId!.Value, DateTimeOffset.UtcNow, 259200, token));
            Assert.IsFalse(await EpssRevisionEligibility.IsPublishableAsync(db, source.Id, first.RevisionId.Value, DateTimeOffset.UtcNow.AddDays(4), 259200, token));
            Assert.AreEqual(EpssStageStatus.Unchanged, (await stage.StageAsync(source.Id, source.Version, new EpssClient(http), EpssRefresh.LicenseEvidence, token)).Status);
            handler.Body = Compress(content + "CVE-2025-1000,0.1,0.2\n");
            await Assert.ThrowsAsync<SqlException>(() => stage.StageAsync(source.Id, source.Version, new EpssClient(http), "test", token));
            handler.Body = good[..^8];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => stage.StageAsync(source.Id, source.Version, new EpssClient(http), "test", token));
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(x => x.Normalized, token));
            handler.Body = good;
            db.IntelligenceRevisions.Add(new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "revoked", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = SHA256.HashData(good), Artifact = good, SchemaVersion = "old", MappingVersion = "old", LicenseEvidence = "test", RecordCount = 1, Revoked = true });
            await db.SaveChangesAsync(token);
            Assert.IsFalse(await EpssRevisionEligibility.IsPublishableAsync(db, source.Id, first.RevisionId.Value, DateTimeOffset.UtcNow, 259200, token));
            Assert.AreEqual(EpssStageStatus.Unavailable, (await stage.StageAsync(source.Id, source.Version, new EpssClient(http), "test", token)).Status);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public required byte[] Body { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Body) });
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }
}
