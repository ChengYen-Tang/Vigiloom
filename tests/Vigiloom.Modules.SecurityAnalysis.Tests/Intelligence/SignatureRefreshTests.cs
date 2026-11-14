using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SignatureRefreshTests(TestContext context)
{
    private const string First = "eb8c53cb08ed103d031fe9c98d1d23e94904839d";

    [TestMethod]
    public Task Evidence_IsBoundedAndChecksEncodingAndIntegrity() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = SignatureRefresh.SchemaVersion, MappingVersion = SignatureRefresh.MappingVersion,
                LicenseEvidence = SignatureRefresh.LicenseEvidence, RecordCount = 1, Normalized = true };
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat("author " + new string('a', 600), 101)));
            var file = new SignatureFile { RevisionId = revision.Id, Path = "yara/example.yar", Kind = 1, Digest = SHA256.HashData(bytes), Content = bytes };
            await using (var db = contexts.CreateDbContext())
            { db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision); db.SignatureFiles.Add(file); await db.SaveChangesAsync(token); }
            var status = new IntelligenceStatus(contexts, TimeProvider.System);
            var evidence = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token);
            Assert.IsNotNull(evidence); Assert.IsTrue(evidence.Truncated); Assert.HasCount(100, evidence.Lines);
            Assert.IsTrue(evidence.Lines[0].Truncated); Assert.AreEqual(512, evidence.Lines[0].Text.Length);
            var secondPage = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token, page: 2);
            Assert.IsNotNull(secondPage); Assert.HasCount(1, secondPage.Lines); Assert.AreEqual(101, secondPage.Lines[0].Number); Assert.IsFalse(secondPage.Truncated);
            var context = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token, 51);
            Assert.IsNotNull(context?.Context); Assert.HasCount(21, context.Context);
            Assert.AreEqual(41, context.Context[0].Number); Assert.AreEqual(61, context.Context[^1].Number);
            Assert.IsTrue(context.ContextHasMore);
            var lastContext = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token, 101);
            Assert.IsNotNull(lastContext?.Context); Assert.AreEqual(101, lastContext.Context[^1].Number); Assert.IsFalse(lastContext.ContextHasMore);
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.SignatureFiles.SingleAsync(token);
                saved.Content = [0xff]; saved.Digest = SHA256.HashData(saved.Content); await db.SaveChangesAsync(token);
            }
            Assert.IsTrue((await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token))!.InvalidEncoding);
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.SignatureFiles.SingleAsync(token);
                saved.Content = [1]; await db.SaveChangesAsync(token);
            }
            Assert.IsFalse((await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token))!.IntegrityValid);
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_StoresVersionAndPreservesItWhenNewLicenseIsInvalid() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var invalid = false;
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{(invalid ? new string('b', 40) : First)}\"}}") };
                using var archive = SignatureArchiveTests.Archive(changed: invalid, commit: invalid ? new string('b', 40) : First);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            var refresh = new SignatureRefresh(contexts, clients, clock);
            Assert.AreEqual(SignatureRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                var revision = await db.IntelligenceRevisions.SingleAsync(token);
                Assert.AreEqual(5, revision.RecordCount);
                Assert.AreEqual(5, await db.SignatureFiles.CountAsync(token));
                Assert.AreEqual(1, await db.IocEntries.CountAsync(token));
                Assert.AreEqual("invalid-hash", (await db.IocEntries.SingleAsync(token)).Reason);
                Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
                var source = await db.IntelligenceSources.SingleAsync(token);
                var files = await new IntelligenceStatus(contexts, clock).GetSignatureFilesAsync(source.Id, revision.Id, 1, token);
                Assert.IsNotNull(files);
                Assert.HasCount(5, files.Items);
                Assert.IsTrue(files.Items.Any(x => x.Kind == "ioc"));
                Assert.IsTrue(files.Items.Any(x => x.Kind == "yara"));
                var evidence = await new IntelligenceStatus(contexts, clock).GetSignatureEvidenceAsync(source.Id, revision.Id, "yara/example.yar", token);
                Assert.IsNotNull(evidence);
                Assert.IsTrue(evidence.IntegrityValid);
                Assert.HasCount(1, evidence.Lines);
                Assert.Contains("author", evidence.Lines[0].Text);
                Assert.IsNull(await new IntelligenceStatus(contexts, clock).GetSignatureEvidenceAsync(source.Id, revision.Id, "iocs/hash-iocs.txt", token));
                Assert.IsNull(await new IntelligenceStatus(contexts, clock).GetSignatureFilesAsync(Guid.CreateVersion7(), revision.Id, 1, token));
            }
            clock.Advance(TimeSpan.FromDays(2));
            invalid = true;
            Assert.AreEqual(SignatureRefreshResult.Failed, await refresh.RunDueAsync(token));
            await using var final = contexts.CreateDbContext();
            Assert.AreEqual(1, await final.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(5, await final.SignatureFiles.CountAsync(token));
            Assert.AreEqual(1, await final.IocEntries.CountAsync(token));
            Assert.AreEqual("Signature-Base license unavailable or changed.", (await final.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_RollsBackFilesAlreadySavedWhenArchiveFails() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                using var archive = SignatureArchiveTests.Archive(lateInvalidEntry: true);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            Assert.AreEqual(SignatureRefreshResult.Failed, await new SignatureRefresh(contexts, clients, TimeProvider.System).RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            Assert.AreEqual(0, await db.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await db.SignatureFiles.CountAsync(token));
            Assert.AreEqual(0, await db.IocEntries.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_RejectsChangedPolicyBeforeSavingDownloadedData() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                using var db = contexts.CreateDbContext();
                db.IntelligenceSources.Single().MaximumRecords = 1;
                db.SaveChanges();
                using var archive = SignatureArchiveTests.Archive();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            Assert.AreEqual(SignatureRefreshResult.Conflict, await new SignatureRefresh(contexts, clients, TimeProvider.System).RunDueAsync(token));
            await using var final = contexts.CreateDbContext();
            Assert.AreEqual(0, await final.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await final.SignatureFiles.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_CancellationLeavesNoPartialRevision() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                cancellation.Cancel();
                using var archive = SignatureArchiveTests.Archive();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() => new SignatureRefresh(contexts, clients, TimeProvider.System).RunDueAsync(cancellation.Token));
            await using var final = contexts.CreateDbContext();
            Assert.AreEqual(0, await final.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await final.IocEntries.CountAsync(token));
            Assert.IsNull((await final.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public Task Issues_CanPageBeyondFiftyThousandLines() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            await using var db = contexts.CreateDbContext();
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = SignatureRefresh.SchemaVersion, MappingVersion = SignatureRefresh.MappingVersion,
                LicenseEvidence = SignatureRefresh.LicenseEvidence, RecordCount = 1, Normalized = true };
            var file = new SignatureFile { RevisionId = revision.Id, Path = "iocs/keywords.txt", Kind = 0, Digest = new byte[32], Content = [1] };
            db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision); db.SignatureFiles.Add(file);
            await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [security].[IocEntries] ([SignatureFileRowId], [LineNumber], [Kind], [Reason])
                SELECT {file.RowId}, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'unsupported', 'unrecognized-ioc-format'
                FROM (SELECT TOP (50001) 1 AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS numbers
                """, token);
            var status = new IntelligenceStatus(contexts, TimeProvider.System);
            var page1000 = await status.GetIocIssuesAsync(source.Id, revision.Id, file.Path, 1000, token);
            var page1001 = await status.GetIocIssuesAsync(source.Id, revision.Id, file.Path, 1001, token);
            Assert.IsNotNull(page1000); Assert.IsTrue(page1000.HasMore);
            Assert.IsNotNull(page1001); Assert.HasCount(1, page1001.Items); Assert.AreEqual(50001, page1001.Items[0].LineNumber);
            Assert.IsFalse(page1001.HasMore);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    { public SecurityAnalysisDbContext CreateDbContext() => new(options); }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request)); }
    }
}
