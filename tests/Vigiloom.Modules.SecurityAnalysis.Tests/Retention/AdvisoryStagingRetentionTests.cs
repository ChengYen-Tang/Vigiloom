using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Retention;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryStagingRetentionTests(TestContext context)
{
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
