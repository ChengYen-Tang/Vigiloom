using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Retention;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryStagingRetentionTests(TestContext context)
{
    [TestMethod]
    public Task Cleanup_PreservesAdvisoryProvenanceAndPublishedWithdrawals() => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        var now = DateTimeOffset.UtcNow; var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
        IntelligenceRevision Revision(int day, byte value, int count) => new() { SourceId = source.Id, UpstreamVersion = value.ToString(), RetrievedAt = now.AddDays(day), Digest = Enumerable.Repeat(value, 32).ToArray(), Artifact = [value], SchemaVersion = AdvisoryRevisionBuilder.SchemaVersion, MappingVersion = AdvisoryRevisionBuilder.MappingVersion, LicenseEvidence = AdvisoryRevisionBuilder.LicenseEvidence, RecordCount = count, Normalized = true };
        var current = Revision(-1, 1, 1); var previous = Revision(-40, 2, 1); var old = Revision(-80, 3, 2); var origin = Revision(-90, 4, 1);
        AdvisoryImport Import(IntelligenceRevision result) => new() { SourceId = source.Id, SourceVersion = new byte[8], WindowStart = new(2025, 1, 1), WindowEnd = new(2025, 1, 2), Phase = 4, ReconciliationPhase = 3, StartedAt = now.AddDays(-100), ResultRevisionId = result.Id };
        var currentImport = Import(current); var previousImport = Import(previous); var oldImport = Import(old); var originImport = Import(origin);
        db.IntelligenceSources.Add(source); db.IntelligenceRevisions.AddRange(current, previous, old, origin);
        db.AdvisoryImports.AddRange(currentImport, previousImport, oldImport, originImport);
        db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = current.Id, ActivatedAt = now });
        db.IntelligenceActivationEvents.AddRange(new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = old.Id, OccurredAt = now.AddDays(-70), Scheduled = true }, new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = previous.Id, OccurredAt = now.AddDays(-30), Scheduled = true }, new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = current.Id, PreviousRevisionId = previous.Id, OccurredAt = now, Scheduled = true });
        await db.SaveChangesAsync(token);
        AdvisoryImportPage Page(AdvisoryImport import, int number) => new() { ImportId = import.Id, Number = number, Phase = 1, Digest = new byte[32], Payload = [1] };
        var ordinaryPage = Page(oldImport, 1); var withdrawnPage = Page(oldImport, 2); var previousPage = Page(previousImport, 1); var originPage = Page(originImport, 1);
        db.AdvisoryImportPages.AddRange(ordinaryPage, withdrawnPage, previousPage, originPage); await db.SaveChangesAsync(token);
        AdvisoryImportEntry Entry(AdvisoryImportPage page, string id, bool withdrawn = false) => new() { ImportId = page.ImportId, PageRowId = page.RowId, AdvisoryId = id, Digest = new byte[32], UpdatedAt = now.AddDays(-70), WithdrawnAt = withdrawn ? now.AddDays(-60) : null };
        var ordinary = Entry(ordinaryPage, "GHSA-0000-0000-0001"); var withdrawn = Entry(withdrawnPage, "GHSA-0000-0000-0002", true); var prevEntry = Entry(previousPage, "GHSA-0000-0000-0003"); var shared = Entry(originPage, "GHSA-0000-0000-0004");
        db.AdvisoryImportEntries.AddRange(ordinary, withdrawn, prevEntry, shared); await db.SaveChangesAsync(token);
        AdvisoryRevisionEntry Member(IntelligenceRevision revision, AdvisoryImportEntry entry) => new() { RevisionId = revision.Id, EntryRowId = entry.RowId, AdvisoryId = entry.AdvisoryId };
        db.AdvisoryRevisionEntries.AddRange(Member(old, ordinary), Member(old, withdrawn), Member(previous, prevEntry), Member(origin, shared), Member(current, shared));
        await db.SaveChangesAsync(token);
        Assert.IsTrue(await AdvisoryRevisionEligibility.IsPublishableAsync(db, source.Id, previous.Id, token));
        Assert.IsTrue(await AdvisoryRevisionEligibility.IsPublishableAsync(db, source.Id, current.Id, token));
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var retention = new SecurityAuditRetention(); var connection = db.Database.GetDbConnection(); var sqlTransaction = tx.GetDbTransaction(); var cutoff = now.AddDays(-30);
        Assert.AreEqual(4L, await retention.CountAsync(connection, sqlTransaction, cutoff, token));
        Assert.AreEqual(4, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 10, token));
        await tx.CommitAsync(token); db.ChangeTracker.Clear();
        Assert.IsNotNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == old.Id, token)).ContentPurgedAt);
        Assert.IsNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == origin.Id, token)).ContentPurgedAt);
        Assert.AreEqual(withdrawn.AdvisoryId, (await db.AdvisoryRevisionEntries.SingleAsync(x => x.RevisionId == old.Id, token)).AdvisoryId);
        Assert.IsTrue(await AdvisoryRevisionEligibility.IsPublishableAsync(db, source.Id, current.Id, token));
        Assert.IsTrue(await AdvisoryRevisionEligibility.IsPublishableAsync(db, source.Id, previous.Id, token));
        Assert.AreEqual(3, await db.AdvisoryImportEntries.CountAsync(token));
        Assert.AreEqual(3, await db.AdvisoryImportPages.CountAsync(token));
    }, context.CancellationToken);

    [TestMethod]
    public Task Cleanup_RemovesFinishedUnreferencedStagingInBatches() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            var now = DateTimeOffset.UtcNow;
            var source = new IntelligenceSource { Key = "github-reviewed" };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "1", RetrievedAt = now, Digest = new byte[32], Artifact = [1], SchemaVersion = "1", MappingVersion = "1", LicenseEvidence = "test", Normalized = true };
            AdvisoryImport Import(int phase) => new() { SourceId = source.Id, SourceVersion = new byte[8], WindowStart = new(2025, 1, 1), WindowEnd = new(2025, 1, 2), Phase = phase, StartedAt = now.AddDays(-40) };
            var abandoned = Import(5); var completed = Import(4); var active = Import(2);
            db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision);
            db.AdvisoryImports.AddRange(abandoned, completed, active);
            await db.SaveChangesAsync(token);
            AdvisoryImportPage Page(Guid id, int number) => new() { ImportId = id, Number = number, Phase = 1, Digest = new byte[32], Payload = [1] };
            var unused = Page(abandoned.Id, 1); var referenced = Page(abandoned.Id, 2); var retained = Page(completed.Id, 1); var inProgress = Page(active.Id, 1);
            db.AdvisoryImportPages.AddRange(unused, referenced, retained, inProgress);
            await db.SaveChangesAsync(token);
            AdvisoryImportEntry Entry(AdvisoryImportPage page, string id) => new() { ImportId = page.ImportId, PageRowId = page.RowId, AdvisoryId = id, Digest = new byte[32], UpdatedAt = now };
            var unreferencedEntry = Entry(unused, "GHSA-0000-0000-0001");
            var referencedEntry = Entry(referenced, "GHSA-0000-0000-0002");
            db.AdvisoryImportEntries.AddRange(unreferencedEntry, referencedEntry, Entry(retained, "GHSA-0000-0000-0003"), Entry(inProgress, "GHSA-0000-0000-0004"));
            await db.SaveChangesAsync(token);
            db.AdvisoryRevisionEntries.Add(new AdvisoryRevisionEntry { RevisionId = revision.Id, EntryRowId = referencedEntry.RowId, AdvisoryId = referencedEntry.AdvisoryId });
            await db.SaveChangesAsync(token);
            var agentId = Guid.CreateVersion7(); var deviceId = Guid.CreateVersion7(); var old = now.AddDays(-40); var recent = now.AddDays(-5);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security.InventoryChanges (AgentId, DeviceId, Source, Generation, ObservedAt, RecordedAt, Kind, [Key], AfterJson) VALUES ({agentId}, {deviceId}, {1}, {1}, {old}, {old}, {1}, {"old"}, {"{}"}), ({agentId}, {deviceId}, {1}, {1}, {recent}, {recent}, {1}, {"recent"}, {"{}"})", token);
            db.IntegrityChanges.AddRange(
                new Vigiloom.Modules.SecurityAnalysis.Domain.Integrity.IntegrityChangeRecord { AgentId = agentId, DeviceId = deviceId, ScopeId = Guid.CreateVersion7(), ScopeRevision = 1, Generation = 1, ObservedAt = old, RecordedAt = old, Kind = 1, Key = "old", AfterJson = "{}" },
                new Vigiloom.Modules.SecurityAnalysis.Domain.Integrity.IntegrityChangeRecord { AgentId = agentId, DeviceId = deviceId, ScopeId = Guid.CreateVersion7(), ScopeRevision = 1, Generation = 1, ObservedAt = recent, RecordedAt = recent, Kind = 1, Key = "recent", AfterJson = "{}" });
            await db.SaveChangesAsync(token);
            var currentRun = Guid.CreateVersion7();
            var finding = new VulnerabilityFinding { DeviceId = deviceId, SourceId = source.Id, IdentityHash = new byte[32], PackageKey = "package", AdvisoryId = "advisory", FirstEvaluatedAt = old, LastEvaluatedAt = old, LastRevisionId = revision.Id, LastRunId = currentRun };
            db.VulnerabilityFindings.Add(finding);
            VulnerabilityEvaluation Evaluation(Guid run, DateTimeOffset time) => new() { RunId = run, FindingId = finding.Id, SourceId = source.Id, RevisionId = revision.Id, AgentId = agentId, HostSnapshotId = Guid.CreateVersion7(), EvaluatedAt = time, MatcherVersion = "windows-test", Ecosystem = "Windows", Architecture = "x64" };
            db.VulnerabilityEvaluations.AddRange(Evaluation(Guid.CreateVersion7(), old), Evaluation(currentRun, old), Evaluation(Guid.CreateVersion7(), recent));
            BaselineObservation Observation(DateTimeOffset time, string activation) => new() { AgentId = agentId, DeviceId = deviceId, BatchId = Guid.CreateVersion7(), SourceId = source.Id, RevisionId = revision.Id, ActivationVersion = activation, CheckId = "check", ObservedAt = time, ReceivedAt = time, OperatingSystem = "Windows" };
            db.BaselineObservations.AddRange(Observation(old.AddDays(-1), "one"), Observation(old, "one"), Observation(old, "ONE"), Observation(old, "two"), Observation(recent, "three"));
            await db.SaveChangesAsync(token);

            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var retention = new SecurityAuditRetention();
            var connection = db.Database.GetDbConnection(); var sqlTransaction = transaction.GetDbTransaction(); var cutoff = now.AddDays(-30);
            Assert.AreEqual(8L, await retention.CountAsync(connection, sqlTransaction, cutoff, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 1, token));
            Assert.AreEqual(0L, await retention.CountAsync(connection, sqlTransaction, cutoff, token));
            await transaction.CommitAsync(token);

            Assert.AreEqual(2, await db.AdvisoryImportPages.AsNoTracking().CountAsync(token));
            Assert.AreEqual(2, await db.AdvisoryImportEntries.AsNoTracking().CountAsync(token));
            Assert.IsTrue(await db.AdvisoryImportEntries.AsNoTracking().AnyAsync(x => x.RowId == referencedEntry.RowId, token));
            Assert.IsTrue(await db.IntelligenceRevisions.AsNoTracking().AnyAsync(x => x.Id == revision.Id, token));
            Assert.AreEqual(3, await db.AdvisoryImports.AsNoTracking().CountAsync(token));
            Assert.AreEqual("recent", (await db.InventoryChanges.AsNoTracking().SingleAsync(token)).Key);
            Assert.AreEqual("recent", (await db.IntegrityChanges.AsNoTracking().SingleAsync(token)).Key);
            Assert.AreEqual(2, await db.VulnerabilityEvaluations.AsNoTracking().CountAsync(token));
            Assert.IsTrue(await db.VulnerabilityEvaluations.AsNoTracking().AnyAsync(x => x.RunId == currentRun, token));
            Assert.AreEqual(4, await db.BaselineObservations.AsNoTracking().CountAsync(token));
        }, context.CancellationToken);
}
