using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Modules.AlertingOperations.Application;

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
    public Task Update_RestoresPurgedCatalogFromSameSource() => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
        var contexts = new ContextFactory(options); var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
        await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
        var clients = new ClientFactory(_ => new(HttpStatusCode.OK) { Content = new StringContent(Catalog, Encoding.UTF8, "application/json") });
        var update = new KevUpdate(contexts, clients, TimeProvider.System);
        Assert.AreEqual(KevUpdateResult.Staged, await update.RunAsync(source.Id, token));
        Guid revisionId;
        await using (var db = contexts.CreateDbContext())
        {
            var revision = await db.IntelligenceRevisions.SingleAsync(token); revisionId = revision.Id;
            revision.ContentPurgedAt = DateTimeOffset.UtcNow; revision.Normalized = false; revision.Artifact = [];
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(KevUpdateResult.Conflict, await update.RunAsync(source.Id, token));
        await using (var db = contexts.CreateDbContext()) await db.KevRecords.ExecuteDeleteAsync(token);
        Assert.AreEqual(KevUpdateResult.Staged, await update.RunAsync(source.Id, token));
        await using var check = contexts.CreateDbContext();
        var restored = await check.IntelligenceRevisions.SingleAsync(token);
        Assert.AreEqual(revisionId, restored.Id); Assert.IsTrue(restored.Normalized); Assert.IsNull(restored.ContentPurgedAt);
        Assert.AreEqual(1, await check.KevRecords.CountAsync(x => x.RevisionId == revisionId, token));
        Assert.AreEqual(revisionId, (await check.IntelligenceCaches.SingleAsync(token)).RevisionId);
    }, context.CancellationToken);

    [TestMethod]
    public Task NormalizationFailure_RollsBackRevisionCacheAndSuccessfulCheck() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new ContextFactory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext())
            {
                await db.Database.MigrateAsync(token);
                db.IntelligenceSources.Add(source);
                await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE [security].[KevRecords] ADD CONSTRAINT [CK_RejectSecondBatchTest] CHECK ([CveId] <> 'CVE-2026-2256');", token);
            }
            using var template = JsonDocument.Parse(Catalog);
            var entry = template.RootElement.GetProperty("vulnerabilities")[0].GetRawText();
            var entries = Enumerable.Range(0, 257).Select(i => entry.Replace("CVE-2026-1234", $"CVE-2026-{2000 + i}", StringComparison.Ordinal));
            var largeCatalog = "{\"catalogVersion\":\"2026.1\",\"dateReleased\":\"2026-11-01T12:30:00Z\",\"count\":257,\"vulnerabilities\":[" + string.Join(',', entries) + "]}";
            var clients = new ClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(largeCatalog, Encoding.UTF8, "application/json") });
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => new KevUpdate(contexts, clients, TimeProvider.System).RunAsync(source.Id, token));
            Assert.IsInstanceOfType<SqlException>(failure.InnerException);
            Assert.AreEqual(547, ((SqlException)failure.InnerException).Number);
            Assert.Contains("CK_RejectSecondBatchTest", failure.InnerException.Message);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(0, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await check.IntelligenceCaches.CountAsync(token));
            Assert.AreEqual(0, await check.KevRecords.CountAsync(token));
            Assert.IsNull((await check.IntelligenceSources.SingleAsync(token)).LastSuccessfulCheckAt);
        }, context.CancellationToken);

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
                var record = await db.KevRecords.SingleAsync(token);
                Assert.AreEqual(revision.Id, record.RevisionId);
                Assert.AreEqual("CVE-2026-1234", record.CveId);
                Assert.AreEqual("Vendor", record.VendorProject);
                Assert.AreEqual(new DateOnly(2026, 12, 1), record.DueDate);
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
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var requests = 0;
            var clients = new ClientFactory(_ =>
            {
                Interlocked.Increment(ref requests);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog, Encoding.UTF8, "application/json") };
            });
            var update = new KevUpdate(contexts, clients, TimeProvider.System);
            var first = Task.Run(() => update.RunAsync(source.Id, token), token);
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10), token));
                Assert.AreEqual(KevUpdateResult.Busy, await update.RunAsync(source.Id, token));
            }
            finally
            {
                release.Set();
                try { await first; } catch { /* Preserve any assertion failure while ensuring the worker has stopped. */ }
            }
            Assert.AreEqual(KevUpdateResult.Staged, await first);
            Assert.AreEqual(1, requests);
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

    [TestMethod]
    public Task Update_NewMapping_ReimportsIdenticalBytesWithoutUsingOldValidators() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new ContextFactory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            var bytes = Encoding.UTF8.GetBytes(Catalog);
            var oldRevision = new IntelligenceRevision
            {
                SourceId = source.Id, UpstreamVersion = "2026.1", ReleasedAt = DateTimeOffset.UtcNow,
                RetrievedAt = DateTimeOffset.UtcNow, Digest = SHA256.HashData(bytes), Artifact = bytes,
                SchemaVersion = KevCatalogValidator.SchemaRevision, MappingVersion = "kev-raw-v1",
                LicenseEvidence = KevUpdate.LicenseEvidence, RecordCount = 1
            };
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                db.IntelligenceRevisions.Add(oldRevision);
                db.IntelligenceCaches.Add(new IntelligenceCache { SourceId = source.Id, RevisionId = oldRevision.Id, ETag = "\"old\"" });
                await db.SaveChangesAsync(token);
            }
            var clients = new ClientFactory(request =>
            {
                Assert.IsFalse(request.Headers.IfNoneMatch.Any());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            });
            Assert.AreEqual(KevUpdateResult.Staged, await new KevUpdate(contexts, clients, TimeProvider.System).RunAsync(source.Id, token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(2, await check.IntelligenceRevisions.CountAsync(token));
            var revision = await check.IntelligenceRevisions.SingleAsync(x => x.MappingVersion == KevUpdate.MappingVersion, token);
            Assert.IsTrue(revision.Normalized);
            Assert.AreEqual(1, await check.KevRecords.CountAsync(x => x.RevisionId == revision.Id, token));
            var original = await check.IntelligenceRevisions.SingleAsync(x => x.Id == oldRevision.Id, token);
            original.Revoked = true;
            await check.SaveChangesAsync(token);
            Assert.AreEqual(KevUpdateResult.Failed, await new KevUpdate(contexts, clients, TimeProvider.System).RunAsync(source.Id, token));
            var publication = new IntelligencePublication(contexts, new OperationAuditWriter(), TimeProvider.System);
            Assert.AreEqual(IntelligencePublicationResult.Unavailable,
                await publication.ActivateAsync(source.Id, revision.Id, null, "", false, Guid.CreateVersion7(), token));
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
