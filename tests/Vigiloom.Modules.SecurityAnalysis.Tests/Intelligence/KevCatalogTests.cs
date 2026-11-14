using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class KevCatalogTests(TestContext context)
{
    [TestMethod]
    public Task Lookup_DistinguishesMissingStaleSelectionChangesAndRevokedContent() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new Factory(options);
            var catalog = new KevCatalog(contexts, TimeProvider.System);
            Assert.AreEqual(KevAvailability.Unavailable, (await catalog.GetActiveAsync(token)).Availability);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            var first = Revision(source.Id, 1);
            var second = Revision(source.Id, 2);
            var staged = Revision(source.Id, 3);
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                db.IntelligenceRevisions.AddRange(first, second, staged);
                db.KevRecords.Add(new KevRecord
                {
                    RevisionId = first.Id, CveId = "CVE-2026-0001", VendorProject = "Vendor", Product = "Product",
                    VulnerabilityName = "Example", DateAdded = new DateOnly(2026, 1, 1), Description = "Description",
                    RequiredAction = "Update", DueDate = new DateOnly(2026, 2, 1), RansomwareUse = "Unknown", CwesJson = "[\"CWE-79\"]"
                });
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(KevAvailability.NoActiveRevision, (await catalog.GetActiveAsync(token)).Availability);
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = first.Id, ActivatedAt = DateTimeOffset.UtcNow });
                db.IntelligenceActivationEvents.Add(Event(source.Id, first.Id));
                db.IntelligenceActivationEvents.Add(Event(source.Id, second.Id));
                await db.SaveChangesAsync(token);
            }
            var snapshot = await catalog.GetActiveAsync(token);
            Assert.AreEqual(KevAvailability.Ready, snapshot.Availability);
            Assert.AreEqual("official-https", snapshot.Origin);
            var found = await catalog.FindCurrentAsync(source.Id, first.Id, snapshot.ActivationVersion!, ["CVE-2026-0001", "CVE-2026-9999"], token);
            Assert.AreEqual(KevAvailability.Ready, found.Availability);
            Assert.AreEqual("official-https", found.Origin);
            Assert.AreEqual(KevAvailability.Ready, (await catalog.FindCurrentAsync(source.Id, first.Id, snapshot.ActivationVersion!, [], token)).Availability);
            var maximumBatch = Enumerable.Range(1, 500).Select(x => $"CVE-2026-{x:0000}").ToArray();
            Assert.HasCount(1, (await catalog.FindCurrentAsync(source.Id, first.Id, snapshot.ActivationVersion!, maximumBatch, token)).Entries);
            Assert.HasCount(1, found.Entries);
            Assert.AreEqual("Unknown", found.Entries[0].RansomwareUse);
            CollectionAssert.AreEqual(new[] { "CWE-79" }, found.Entries[0].Cwes!.ToArray());
            Assert.AreEqual(KevAvailability.Unavailable, (await catalog.FindHistoricalAsync(source.Id, staged.Id, ["CVE-2026-0001"], token)).Availability);
            await using (var db = contexts.CreateDbContext())
            {
                var active = await db.IntelligenceActivations.SingleAsync(token);
                active.RevisionId = second.Id;
                await db.SaveChangesAsync(token);
                active.RevisionId = first.Id;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(KevAvailability.SelectionChanged,
                (await catalog.FindCurrentAsync(source.Id, first.Id, snapshot.ActivationVersion!, ["CVE-2026-0001"], token)).Availability);
            snapshot = await catalog.GetActiveAsync(token);
            await using (var db = contexts.CreateDbContext())
            {
                var item = await db.IntelligenceSources.SingleAsync(token);
                item.LastSuccessfulCheckAt = DateTimeOffset.UtcNow.AddDays(-4);
                await db.SaveChangesAsync(token);
            }
            var stale = await catalog.FindCurrentAsync(source.Id, first.Id, snapshot.ActivationVersion!, ["CVE-2026-0001"], token);
            Assert.AreEqual(KevAvailability.Stale, stale.Availability);
            Assert.IsEmpty(stale.Entries);
            var history = await catalog.FindHistoricalAsync(source.Id, first.Id, ["CVE-2026-0001"], token);
            Assert.AreEqual(KevAvailability.Stale, history.Availability);
            Assert.IsTrue(history.Historical);
            Assert.HasCount(1, history.Entries);
            await using (var db = contexts.CreateDbContext())
            {
                var item = await db.IntelligenceSources.SingleAsync(token);
                item.Enabled = false;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(KevAvailability.Disabled, (await catalog.FindHistoricalAsync(source.Id, first.Id, ["CVE-2026-0001"], token)).Availability);
            await using (var db = contexts.CreateDbContext())
            {
                var item = await db.IntelligenceSources.SingleAsync(token);
                item.Enabled = true;
                item.LastSuccessfulCheckAt = DateTimeOffset.UtcNow;
                var oldMapping = Revision(source.Id, 1);
                oldMapping.MappingVersion = "kev-raw-v1";
                oldMapping.Revoked = true;
                db.IntelligenceRevisions.Add(oldMapping);
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(KevAvailability.Unavailable, (await catalog.GetActiveAsync(token)).Availability);
            Assert.AreEqual(KevAvailability.Unavailable, (await catalog.FindHistoricalAsync(source.Id, first.Id, ["CVE-2026-0001"], token)).Availability);
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.FindHistoricalAsync(source.Id, first.Id, ["not-a-cve"], token));
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.FindHistoricalAsync(source.Id, first.Id,
                Enumerable.Range(1000, 501).Select(x => $"CVE-2026-{x}").ToArray(), token));
        }, context.CancellationToken);

    private static IntelligenceRevision Revision(Guid sourceId, byte digest) => new()
    {
        SourceId = sourceId, UpstreamVersion = $"v{digest}", ReleasedAt = DateTimeOffset.UtcNow, RetrievedAt = DateTimeOffset.UtcNow,
        Digest = Enumerable.Repeat(digest, 32).ToArray(), Artifact = [digest], SchemaVersion = KevCatalogValidator.SchemaRevision,
        MappingVersion = KevUpdate.MappingVersion, LicenseEvidence = KevUpdate.LicenseEvidence, RecordCount = 1, Normalized = true
    };

    private static IntelligenceActivationEvent Event(Guid sourceId, Guid revisionId) => new()
    {
        SourceId = sourceId, RevisionId = revisionId, ActorId = Guid.CreateVersion7(), OccurredAt = DateTimeOffset.UtcNow
    };

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
}
