using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class KevUpdateTests(TestContext context)
{
    private const string Catalog = """
        {"catalogVersion":"2026.1","dateReleased":"2026-11-01T12:30:00Z","count":1,"vulnerabilities":[
        {"cveID":"CVE-2026-1234","vendorProject":"Vendor","product":"Product","vulnerabilityName":"Example",
         "dateAdded":"2026-11-01","shortDescription":"Description","requiredAction":"Update","dueDate":"2026-12-01"}]}
        """;

    [TestMethod]
    public Task Update_StagesValidDataAndPreservesActiveRevisionOnInvalidData() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new ContextFactory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var setup = contexts.CreateDbContext())
            {
                setup.IntelligenceSources.Add(source);
                await setup.SaveChangesAsync(token);
            }
            var payload = Catalog;
            var status = HttpStatusCode.OK;
            var clients = new ClientFactory(_ =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"version-1\"");
                if (status == HttpStatusCode.OK) response.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                return response;
            });
            var update = new KevUpdate(contexts, clients, TimeProvider.System);
            Assert.AreEqual(KevUpdateResult.Staged, await update.RunAsync(source.Id, token));
            status = HttpStatusCode.NotModified;
            Assert.AreEqual(KevUpdateResult.Unchanged, await update.RunAsync(source.Id, token));
            status = HttpStatusCode.OK;
            Guid revisionId;
            await using (var db = contexts.CreateDbContext())
            {
                var revision = await db.IntelligenceRevisions.SingleAsync(token);
                revisionId = revision.Id;
                db.IntelligenceActivations.Add(new IntelligenceActivation
                    { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(token);
            }
            payload = "{invalid json";
            Assert.AreEqual(KevUpdateResult.Failed, await update.RunAsync(source.Id, token));
            await using (var db = contexts.CreateDbContext())
            {
                Assert.AreEqual(revisionId, (await db.IntelligenceActivations.SingleAsync(token)).RevisionId);
                Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(token));
                Assert.IsNotNull((await db.IntelligenceSources.SingleAsync(token)).LastError);
            }
        }, context.CancellationToken);

    [TestMethod]
    public Task Update_ConfigurationChangedDuringDownload_DoesNotStageData() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new ContextFactory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                await db.SaveChangesAsync(token);
            }
            var clients = new ClientFactory(_ =>
            {
                using var db = contexts.CreateDbContext();
                db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdate(s => s.SetProperty(x => x.Enabled, false));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog, Encoding.UTF8, "application/json") };
            });
            var update = new KevUpdate(contexts, clients, TimeProvider.System);
            Assert.AreEqual(KevUpdateResult.Conflict, await update.RunAsync(source.Id, token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(0, await check.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    private sealed class ContextFactory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    [TestMethod]
    public Task Update_RevokedCachedVersion_DoesNotSendConditionalValidators() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new ContextFactory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                await db.SaveChangesAsync(token);
            }
            var calls = 0;
            var clients = new ClientFactory(request =>
            {
                calls++;
                if (calls == 2) Assert.IsFalse(request.Headers.IfNoneMatch.Any());
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog, Encoding.UTF8, "application/json") };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"version-1\"");
                return response;
            });
            var update = new KevUpdate(contexts, clients, TimeProvider.System);
            Assert.AreEqual(KevUpdateResult.Staged, await update.RunAsync(source.Id, token));
            await using (var db = contexts.CreateDbContext())
            {
                var revision = await db.IntelligenceRevisions.SingleAsync(token);
                revision.Revoked = true;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(KevUpdateResult.Failed, await update.RunAsync(source.Id, token));
            Assert.AreEqual(2, calls);
        }, context.CancellationToken);

    [TestMethod]
    public Task Update_ConcurrentDownloads_DoNotCreateDuplicateRevisions() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new ContextFactory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                await db.SaveChangesAsync(token);
            }
            using var barrier = new Barrier(2);
            var clients = new ClientFactory(_ =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog, Encoding.UTF8, "application/json") };
            });
            var update = new KevUpdate(contexts, clients, TimeProvider.System);
            var results = await Task.WhenAll(Task.Run(() => update.RunAsync(source.Id, token), token),
                Task.Run(() => update.RunAsync(source.Id, token), token));
            CollectionAssert.AreEquivalent(new[] { KevUpdateResult.Staged, KevUpdateResult.Conflict }, results);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(1, await check.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Update_CachedRevisionRevokedBefore304_DoesNotMarkCheckSuccessful() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new ContextFactory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                await db.SaveChangesAsync(token);
            }
            var calls = 0;
            var clients = new ClientFactory(request =>
            {
                calls++;
                if (calls == 1)
                {
                    var initial = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog, Encoding.UTF8, "application/json") };
                    initial.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"version-1\"");
                    return initial;
                }
                Assert.IsTrue(request.Headers.IfNoneMatch.Any());
                using var db = contexts.CreateDbContext();
                db.IntelligenceRevisions.Where(x => x.SourceId == source.Id).ExecuteUpdate(s => s.SetProperty(x => x.Revoked, true));
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
            var update = new KevUpdate(contexts, clients, TimeProvider.System);
            Assert.AreEqual(KevUpdateResult.Staged, await update.RunAsync(source.Id, token));
            DateTimeOffset? lastSuccess;
            Guid cachedRevision;
            await using (var db = contexts.CreateDbContext())
            {
                lastSuccess = (await db.IntelligenceSources.SingleAsync(token)).LastSuccessfulCheckAt;
                cachedRevision = (await db.IntelligenceCaches.SingleAsync(token)).RevisionId;
            }
            Assert.AreEqual(KevUpdateResult.Conflict, await update.RunAsync(source.Id, token));
            Assert.AreEqual(2, calls);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(lastSuccess, (await check.IntelligenceSources.SingleAsync(token)).LastSuccessfulCheckAt);
            var cache = await check.IntelligenceCaches.SingleAsync(token);
            Assert.AreEqual(cachedRevision, cache.RevisionId);
            Assert.AreEqual("\"version-1\"", cache.ETag);
            Assert.AreEqual(1, await check.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
