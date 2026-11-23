using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.IO.Compression;
using System.Security.Cryptography;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Retention;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RepositoryContentRetentionTests(TestContext context)
{
    [TestMethod]
    public Task Cleanup_KeepsCurrentPreviousAndOfflineKevCatalogs() => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        var now = DateTimeOffset.UtcNow; var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = now.AddDays(-30) };
        IntelligenceRevision Revision(int day, byte value) => new() { SourceId = source.Id, UpstreamVersion = value.ToString(), RetrievedAt = now.AddDays(day), Digest = Enumerable.Repeat(value, 32).ToArray(), Artifact = [value], SchemaVersion = KevCatalogValidator.SchemaRevision, MappingVersion = KevUpdate.MappingVersion, LicenseEvidence = KevUpdate.LicenseEvidence, Normalized = true };
        var current = Revision(-1, 1); var previous = Revision(-50, 2); var unused = Revision(-80, 3); var offline = Revision(-90, 4);
        db.IntelligenceSources.Add(source); db.IntelligenceRevisions.AddRange(current, previous, unused, offline);
        db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = current.Id, ActivatedAt = now });
        db.IntelligenceActivationEvents.AddRange(new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = previous.Id, OccurredAt = now.AddDays(-40), Scheduled = true }, new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = current.Id, PreviousRevisionId = previous.Id, OccurredAt = now, Scheduled = true });
        db.OfflineImports.Add(new OfflineImport { SourceId = source.Id, RevisionId = offline.Id, ActorId = Guid.CreateVersion7(), ImportedAt = now });
        db.KevRecords.Add(new KevRecord { RevisionId = unused.Id, CveId = "CVE-2026-1234", VendorProject = "Vendor", Product = "Product", VulnerabilityName = "Test", Description = "Test", RequiredAction = "Update" });
        await db.SaveChangesAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var retention = new SecurityAuditRetention(); var connection = db.Database.GetDbConnection(); var sqlTransaction = transaction.GetDbTransaction();
        Assert.AreEqual(2L, await retention.CountAsync(connection, sqlTransaction, now.AddDays(-30), token));
        Assert.AreEqual(2, await retention.DeleteBatchAsync(connection, sqlTransaction, now.AddDays(-30), 10, token));
        await transaction.CommitAsync(token); db.ChangeTracker.Clear();
        var removed = await db.IntelligenceRevisions.SingleAsync(x => x.Id == unused.Id, token);
        Assert.IsNotNull(removed.ContentPurgedAt); Assert.IsEmpty(removed.Artifact);
        Assert.IsNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == previous.Id, token)).ContentPurgedAt);
        Assert.IsNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == offline.Id, token)).ContentPurgedAt);
        Assert.AreEqual(0, await db.KevRecords.CountAsync(token));
    }, context.CancellationToken);

    [TestMethod]
    public Task Cleanup_SkipsUnusablePriorAndPreservesEarlierUsableRevision() => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        var now = DateTimeOffset.UtcNow; var source = new IntelligenceSource { Key = "signature-base", Enabled = true };
        IntelligenceRevision Revision(int day, byte value) { var artifact = new[] { value }; return new() { SourceId = source.Id, UpstreamVersion = new string((char)('a' + value), 40), RetrievedAt = now.AddDays(day), Digest = SHA256.HashData(artifact), Artifact = artifact, SchemaVersion = SignatureRefresh.SchemaVersion, MappingVersion = SignatureRefresh.MappingVersion, LicenseEvidence = SignatureRefresh.LicenseEvidence, RecordCount = 4, Normalized = true }; }
        var current = Revision(-1, 1); var invalid = Revision(-40, 2); var usable = Revision(-50, 3); var unused = Revision(-80, 4);
        db.IntelligenceSources.Add(source); db.IntelligenceRevisions.AddRange(current, invalid, usable, unused);
        db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = current.Id, ActivatedAt = now });
        db.IntelligenceActivationEvents.AddRange(
            new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = usable.Id, OccurredAt = now.AddDays(-45), Scheduled = true },
            new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = invalid.Id, PreviousRevisionId = usable.Id, OccurredAt = now.AddDays(-35), Scheduled = true },
            new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = current.Id, PreviousRevisionId = invalid.Id, OccurredAt = now, Scheduled = true });
        foreach (var (path, fixture, kind, compressed) in new[] { ("LICENSE", "LICENSE", 2, false), ("README.md", "README.md", 2, false), (IocSourceEligibility.HashPath, "hash-iocs.txt.gz", 0, true), (IocSourceEligibility.C2Path, "c2-iocs.txt.gz", 0, true) })
        {
            using var input = GetType().Assembly.GetManifestResourceStream($"Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence.Fixtures.SignatureBase.{fixture}")!;
            using var output = new MemoryStream();
            if (compressed) { using var gzip = new GZipStream(input, CompressionMode.Decompress); await gzip.CopyToAsync(output, token); }
            else await input.CopyToAsync(output, token);
            var bytes = output.ToArray(); db.SignatureFiles.Add(new SignatureFile { RevisionId = usable.Id, Path = path, Kind = kind, Content = bytes, Digest = SHA256.HashData(bytes) });
        }
        db.SignatureFiles.Add(new SignatureFile { RevisionId = unused.Id, Path = "unused", Kind = 0, Content = [1], Digest = new byte[32] });
        await db.SaveChangesAsync(token);
        Assert.IsTrue(await IocSourceEligibility.IsEligibleAsync(db, source.Id, usable.Id, token));
        Assert.IsFalse(await IocSourceEligibility.IsEligibleAsync(db, source.Id, invalid.Id, token));
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var retention = new SecurityAuditRetention(); var connection = db.Database.GetDbConnection(); var sqlTransaction = transaction.GetDbTransaction();
        Assert.AreEqual(3L, await retention.CountAsync(connection, sqlTransaction, now.AddDays(-30), token));
        Assert.AreEqual(3, await retention.DeleteBatchAsync(connection, sqlTransaction, now.AddDays(-30), 10, token));
        await transaction.CommitAsync(token);
        db.ChangeTracker.Clear();
        Assert.IsNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == usable.Id, token)).ContentPurgedAt);
        Assert.IsNotNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == unused.Id, token)).ContentPurgedAt);
    }, context.CancellationToken);

    [TestMethod]
    public Task Cleanup_PreservesCurrentPreviousAndReferencedVersions() => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var source = new IntelligenceSource { Key = "signature-base", Enabled = true };
        IntelligenceRevision Revision(int day, byte digest) => new() { SourceId = source.Id, UpstreamVersion = digest.ToString(), RetrievedAt = now.AddDays(day), Digest = Enumerable.Repeat(digest, 32).ToArray(), Artifact = [digest], SchemaVersion = "1", MappingVersion = "1", LicenseEvidence = "test", Normalized = true };
        var current = Revision(-1, 1); var previous = Revision(-60, 2); var unused = Revision(-80, 3); var evidence = Revision(-90, 4); var unusedSecond = Revision(-100, 5);
        source.LastObservedRevisionId = previous.Id;
        db.IntelligenceSources.Add(source);
        db.IntelligenceRevisions.AddRange(current, previous, unused, evidence, unusedSecond);
        db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = current.Id, ActivatedAt = now });
        db.IntelligenceActivationEvents.AddRange(
            new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = previous.Id, OccurredAt = now.AddDays(-50), Scheduled = true },
            new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = current.Id, PreviousRevisionId = previous.Id, OccurredAt = now, Scheduled = true });
        db.IocDetections.Add(new IocDetection { AgentId = Guid.CreateVersion7(), DeviceId = Guid.CreateVersion7(), MessageId = Guid.CreateVersion7(), SourceId = source.Id, RevisionId = evidence.Id, Kind = "domain", Value = "example.test", Field = "message", SourceLine = 1, ReceivedAt = now, EvaluatedAt = now });
        var file = new SignatureFile { RevisionId = unused.Id, Path = "iocs/test.txt", Kind = 0, Digest = new byte[32], Content = [1] };
        db.SignatureFiles.Add(file);
        db.SignatureFiles.Add(new SignatureFile { RevisionId = unusedSecond.Id, Path = "iocs/second.txt", Kind = 0, Digest = new byte[32], Content = [1] });
        await db.SaveChangesAsync(token);
        db.IocEntries.Add(new IocEntry { SignatureFileRowId = file.RowId, LineNumber = 1, Kind = "domain", Value = "example.test" });
        await db.SaveChangesAsync(token);

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var retention = new SecurityAuditRetention(); var connection = db.Database.GetDbConnection(); var sqlTransaction = transaction.GetDbTransaction(); var cutoff = now.AddDays(-30);
        Assert.AreEqual(5L, await retention.CountAsync(connection, sqlTransaction, cutoff, token));
        Assert.AreEqual(5, await retention.DeleteBatchAsync(connection, sqlTransaction, cutoff, 10, token));
        Assert.AreEqual(0L, await retention.CountAsync(connection, sqlTransaction, cutoff, token));
        await transaction.CommitAsync(token);
        db.ChangeTracker.Clear();
        Assert.IsNotNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == unused.Id, token)).ContentPurgedAt);
        Assert.IsNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == previous.Id, token)).ContentPurgedAt);
        Assert.IsNull((await db.IntelligenceRevisions.SingleAsync(x => x.Id == evidence.Id, token)).ContentPurgedAt);
        Assert.IsTrue(await db.IntelligenceActivationEvents.AnyAsync(x => x.RevisionId == previous.Id, token));
        Assert.AreEqual(0, await db.SignatureFiles.CountAsync(token));
        Assert.AreEqual(0, await db.IocEntries.CountAsync(token));
    }, context.CancellationToken);
}
