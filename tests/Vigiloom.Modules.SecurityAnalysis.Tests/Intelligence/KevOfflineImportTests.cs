using System.Data.Common;
using System.Text;
using System.Net;
using Microsoft.EntityFrameworkCore;
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
public sealed class KevOfflineImportTests(TestContext context)
{
    private static readonly byte[] Catalog = Encoding.UTF8.GetBytes("""
        {"catalogVersion":"v1","dateReleased":"2026-11-14T12:00:00Z","count":1,"vulnerabilities":[
        {"cveID":"CVE-2026-1234","vendorProject":"Vendor","product":"Product","vulnerabilityName":"Example",
         "dateAdded":"2026-11-01","shortDescription":"Description","requiredAction":"Update","dueDate":"2026-12-01"}]}
        """);

    [TestMethod]
    public Task Import_DoesNotReadASecondFileWhileSourceIsBusy() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var import = new KevOfflineImport(contexts, new OperationAuditWriter(), TimeProvider.System);
            using var blocked = new BlockingStream(Catalog);
            var first = import.ImportAsync(source.Id, blocked, Guid.CreateVersion7(), token);
            using var second = new MemoryStream(Catalog);
            Exception? failure = null;
            KevOfflineImportResult firstResult = default;
            try
            {
                await blocked.Started.Task.WaitAsync(token);
                Assert.AreEqual(KevOfflineImportResult.Busy, await import.ImportAsync(source.Id, second, Guid.CreateVersion7(), token));
                Assert.AreEqual(0L, second.Position);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                blocked.Release.TrySetResult();
                try { firstResult = await first; }
                catch (Exception cleanup) when (failure is not null) { throw new AggregateException(failure, cleanup); }
            }
            Assert.AreEqual(KevOfflineImportResult.Imported, firstResult);
        }, context.CancellationToken);

    [TestMethod]
    public Task Import_ValidatesAndAuditsWithoutPretendingToCheckTheRemoteSource() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var actor = Guid.CreateVersion7();
            var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
            var clock = new TestClock(now);
            var failed = new KevOfflineImport(contexts, new FailingAudit(), clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failed.ImportAsync(source.Id, new MemoryStream(Catalog), actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                Assert.AreEqual(0, await db.IntelligenceRevisions.CountAsync(token));
                Assert.AreEqual(0, await db.KevRecords.CountAsync(token));
                Assert.AreEqual(0, await db.OfflineImports.CountAsync(token));
            }
            var import = new KevOfflineImport(contexts, new OperationAuditWriter(), clock);
            Assert.AreEqual(KevOfflineImportResult.Invalid, await import.ImportAsync(source.Id, new MemoryStream("invalid"u8.ToArray()), actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceSources.SingleAsync(token)).MaximumBytes = 1024;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(KevOfflineImportResult.Invalid, await import.ImportAsync(source.Id, new MemoryStream(new byte[1025]), actor, token));
            Assert.AreEqual(KevOfflineImportResult.Imported, await import.ImportAsync(source.Id, new MemoryStream(Catalog), actor, token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(1, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(1, await check.KevRecords.CountAsync(token));
            Assert.AreEqual(actor, (await check.OfflineImports.SingleAsync(token)).ActorId);
            Assert.IsNull((await check.IntelligenceSources.SingleAsync(token)).LastSuccessfulCheckAt);
            Assert.AreEqual(0, await check.IntelligenceCaches.CountAsync(token));
            Assert.AreEqual(0, await check.IntelligenceActivations.CountAsync(token));
            var revision = await check.IntelligenceRevisions.SingleAsync(token);
            Assert.AreEqual(KevUpdate.OfflineMappingVersion, revision.MappingVersion);
            Assert.AreEqual(KevUpdate.OfflineLicenseEvidence, revision.LicenseEvidence);
            var publication = new IntelligencePublication(contexts, new OperationAuditWriter(), clock);
            Assert.AreEqual(IntelligencePublicationResult.OfflineConfirmationRequired,
                await publication.ActivateAsync(source.Id, revision.Id, null, "", false, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Published,
                await publication.ActivateAsync(source.Id, revision.Id, null, "", false, true, actor, token));
            var active = await new KevCatalog(contexts, clock).GetActiveAsync(token);
            Assert.AreEqual(KevAvailability.Ready, active.Availability);
            Assert.IsNull(active.LastSuccessfulCheckAt);
            Assert.AreEqual("administrator-import-unverified", active.Origin);
            Assert.AreEqual(KevUpdate.OfflineLicenseEvidence, active.LicenseEvidence);
            var lookup = await new KevCatalog(contexts, clock).FindCurrentAsync(source.Id, revision.Id, active.ActivationVersion!, ["CVE-2026-1234"], token);
            Assert.AreEqual("administrator-import-unverified", lookup.Origin);
            Assert.AreEqual(KevUpdate.OfflineMappingVersion, lookup.MappingVersion);
            Assert.IsFalse((await new IntelligenceStatus(contexts, clock).GetSourcesAsync(1, token)).Items.Single().IsStale);
            clock.Advance(TimeSpan.FromDays(4));
            Assert.AreEqual(KevAvailability.Stale, (await new KevCatalog(contexts, clock).GetActiveAsync(token)).Availability);
            Assert.IsTrue((await new IntelligenceStatus(contexts, clock).GetSourcesAsync(1, token)).Items.Single().IsStale);
            Assert.AreEqual(KevOfflineImportResult.Imported, await import.ImportAsync(source.Id, new MemoryStream(Catalog), actor, token));
            Assert.AreEqual(KevAvailability.Stale, (await new KevCatalog(contexts, clock).GetActiveAsync(token)).Availability);
            Assert.AreEqual(KevUpdateResult.Staged, await new KevUpdate(contexts, new CatalogClient(), clock).RunAsync(source.Id, token));
            await using var afterOnline = contexts.CreateDbContext();
            Assert.AreEqual(2, await afterOnline.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(1, await afterOnline.IntelligenceRevisions.CountAsync(x => x.MappingVersion == KevUpdate.MappingVersion && x.LicenseEvidence == KevUpdate.LicenseEvidence, token));
            var onlineRevision = await afterOnline.IntelligenceRevisions.SingleAsync(x => x.MappingVersion == KevUpdate.MappingVersion, token);
            Assert.AreEqual(IntelligenceRevocationResult.Revoked, await new IntelligenceRevocation(contexts, new OperationAuditWriter(), clock)
                .RevokeAsync(source.Id, revision.Id, actor, token));
            Assert.AreEqual(KevOfflineImportResult.Invalid, await import.ImportAsync(source.Id, new MemoryStream(Catalog), actor, token));
            var version = Convert.ToBase64String((await afterOnline.IntelligenceActivations.AsNoTracking().SingleAsync(token)).Version);
            Assert.AreEqual(IntelligencePublicationResult.Unavailable,
                await publication.ActivateAsync(source.Id, onlineRevision.Id, revision.Id, version, false, actor, token));
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    { public SecurityAnalysisDbContext CreateDbContext() => new(options); }
    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    { public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken token) => throw new InvalidOperationException("audit failed"); }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan elapsed) => now += elapsed; }
    private sealed class CatalogClient : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Catalog) });
        }
    }
    private sealed class BlockingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return await inner.ReadAsync(buffer, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
