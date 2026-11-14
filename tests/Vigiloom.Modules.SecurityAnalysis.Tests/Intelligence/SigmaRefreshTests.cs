using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SigmaRefreshTests(TestContext context)
{
    private const string First = "1111111111111111111111111111111111111111";
    private const string Second = "2222222222222222222222222222222222222222";

    [TestMethod]
    public Task Refresh_StagesCompleteVersionAndKeepsItWhenNextArchiveIsInvalid() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var commit = First;
            var invalid = false;
            var wrongLicense = false;
            var archiveRequests = 0;
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{commit}\"}}") };
                archiveRequests++;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(commit, invalid, wrongLicense)) };
            });
            var refresh = new SigmaRefresh(contexts, clients, clock);
            Assert.AreEqual(SigmaRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var check = contexts.CreateDbContext())
            {
                var source = await check.IntelligenceSources.SingleAsync(token);
                var revision = await check.IntelligenceRevisions.SingleAsync(token);
                Assert.AreEqual("sigmahq-rules", source.Key);
                Assert.AreEqual(First, revision.UpstreamVersion);
                Assert.AreEqual(2, revision.RecordCount);
                Assert.AreEqual(2, await check.SigmaRules.CountAsync(token));
                Assert.IsNull((await check.SigmaRules.SingleAsync(x => x.RuleId == null, token)).License);
                Assert.AreEqual(0, await check.IntelligenceActivations.CountAsync(token));
                var rules = await new IntelligenceStatus(contexts, clock).GetSigmaRulesAsync(source.Id, revision.Id, 1, token);
                Assert.IsNotNull(rules);
                Assert.HasCount(2, rules.Items);
                Assert.IsTrue(rules.Items.Any(x => x.Path.StartsWith("rules-threat-hunting/", StringComparison.Ordinal)));
                Assert.AreEqual("windows", rules.Items.Single(x => x.RuleId == "abc").Product);
                Assert.IsNull(await new IntelligenceStatus(contexts, clock).GetSigmaRulesAsync(Guid.CreateVersion7(), revision.Id, 1, token));
            }
            clock.Advance(TimeSpan.FromDays(2));
            Assert.AreEqual(SigmaRefreshResult.Unchanged, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, archiveRequests);
            clock.Advance(TimeSpan.FromDays(2));
            commit = Second;
            invalid = true;
            Assert.AreEqual(SigmaRefreshResult.Failed, await refresh.RunDueAsync(token));
            await using (var final = contexts.CreateDbContext())
            {
                Assert.AreEqual(1, await final.IntelligenceRevisions.CountAsync(token));
                Assert.AreEqual(2, await final.SigmaRules.CountAsync(token));
                Assert.AreEqual("Sigma source failed validation.", (await final.IntelligenceSources.SingleAsync(token)).LastError);
            }
            clock.Advance(TimeSpan.FromHours(1));
            invalid = false;
            wrongLicense = true;
            Assert.AreEqual(SigmaRefreshResult.Failed, await refresh.RunDueAsync(token));
            await using var licenseCheck = contexts.CreateDbContext();
            Assert.AreEqual(1, await licenseCheck.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual("Sigma license unavailable or changed.", (await licenseCheck.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public Task Migration_RefusesToDiscardRetainedRules() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "sigmahq-rules", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            db.SigmaRules.Add(new SigmaRule { RevisionId = revision.Id, Path = "rules/example.yml", Title = "Example", Digest = new byte[32], Content = [1] });
            await db.SaveChangesAsync(token);
            var target = db.Database.GetMigrations().Single(x => x.EndsWith("_IntelligenceSourceLimits", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(target, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(1, await db.SigmaRules.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_DiscardsDownloadWhenPolicyChanges() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                using var db = contexts.CreateDbContext();
                db.IntelligenceSources.Single().MaximumRecords = 1;
                db.SaveChanges();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, invalid: false, wrongLicense: false)) };
            });
            var result = await new SigmaRefresh(contexts, clients, TimeProvider.System).RunDueAsync(token);
            Assert.AreEqual(SigmaRefreshResult.Conflict, result);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(1, (await check.IntelligenceSources.SingleAsync(token)).MaximumRecords);
            Assert.AreEqual(0, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await check.SigmaRules.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_CancellationDoesNotPublishPartialData() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                cancellation.Cancel();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, invalid: false, wrongLicense: false)) };
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() => new SigmaRefresh(contexts, clients, TimeProvider.System).RunDueAsync(cancellation.Token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(0, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await check.SigmaRules.CountAsync(token));
            Assert.IsNull((await check.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public async Task Archive_RejectsUnreviewedNestedLicense()
    {
        using var archive = new MemoryStream(Archive(First, invalid: false, wrongLicense: false, nestedLicense: true));
        await Assert.ThrowsAsync<SigmaLicenseException>(() => SigmaArchive.ReadAsync(archive, First, 50_000,
            (_, _, _, _) => Task.CompletedTask, context.CancellationToken));
    }

    private static byte[] Archive(string commit, bool invalid, bool wrongLicense, bool nestedLicense = false)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry([new("comment", commit)]));
            Write($"sigma-{commit}/LICENSE", wrongLicense ? "changed" : "# Licenses\n\nThe content of this repository is released under the following licenses:\n\n- The Sigma specification (https://github.com/SigmaHQ/sigma-specification) and the Sigma logo are public domain\n- The rules contained in the SigmaHQ repository (https://github.com/SigmaHQ) are released under the [Detection Rule License (DRL) 1.1](https://github.com/SigmaHQ/Detection-Rule-License)\n");
            Write($"sigma-{commit}/rules/example.yml", "title: Example\nid: abc\nauthor: Author\nlogsource: {product: windows}\ndetection: {condition: selection}\n");
            Write($"sigma-{commit}/rules-threat-hunting/other.yml", "title: Hunting\nlogsource: {product: linux}\ndetection: {condition: selection}\n");
            if (nestedLicense) Write($"sigma-{commit}/rules/windows/LICENSE", "different terms");
            if (invalid)
            {
                for (var i = 0; i < 18; i++) Write($"sigma-{commit}/rules/extra-{i}.yml", "title: Extra\nlogsource: {}\ndetection: {}\n");
                Write($"sigma-{commit}/rules/../bad.yml", "bad");
            }
            void Write(string name, string text)
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = content });
            }
        }
        return output.ToArray();
    }

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    { public SecurityAnalysisDbContext CreateDbContext() => new(options); }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan value) => now += value; }
    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request)); }
    }
}
