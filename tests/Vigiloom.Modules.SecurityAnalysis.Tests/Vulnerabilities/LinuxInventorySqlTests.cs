using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Security.Cryptography;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Vulnerabilities;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LinuxInventorySqlTests(TestContext context)
{
    private sealed class FailingNotificationWriter : INotificationWriter
    {
        public Task WriteAsync(NotificationRequest request, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            throw new InvalidOperationException("Simulated notification failure");
        public Task WriteBatchAsync(IReadOnlyList<NotificationRequest> requests, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            throw new InvalidOperationException("Simulated notification failure");
    }
    [TestMethod]
    public Task Debian_TracksPackageFixAndReappearance() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var operationOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var operations = new OperationsDbContext(operationOptions)) await operations.Database.MigrateAsync(token);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            await Publish(InventorySource.HostSystem, InventoryChunkCodec.SchemaVersion,
                new InventoryEntry("host", "host", "6.1", "x64", OperatingSystem: "Linux", ProcessArchitecture: "x64",
                    Runtime: ".NET 10", AvailableProcessorCount: 2, OperatingSystemFamily: "Linux", DistributionId: "debian", DistributionVersion: "12"), 1);
            await PublishPackage("1.0-1", 1);
            var source = new IntelligenceSource { Key = DebianRevisionStage.SourceKey, Enabled = true, LastSuccessfulCheckAt = now };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "test", RetrievedAt = now,
                Digest = SHA256.HashData([7]), Artifact = [7], SchemaVersion = DebianRevisionStage.SchemaVersion,
                MappingVersion = DebianRevisionStage.MappingVersion, LicenseEvidence = DebianRefresh.LicenseEvidence, RecordCount = 1, Normalized = true };
            source.LastObservedRevisionId = revision.Id;
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = now });
            db.DebianAdvisories.Add(new DebianAdvisory { RevisionId = revision.Id, Release = "bookworm", SourcePackage = "openssl",
                AdvisoryId = "CVE-2025-1234", State = (int)DebianAdvisoryState.FixedVersion, FixedVersion = "2.0-1" });
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            var evaluator = new DebianPackageEvaluation(new Factory(options), TimeProvider.System);
            var writer = new LinuxAssessmentWriter(new Factory(options), new OwnerFence(true), TimeProvider.System, new NotificationWriter());
            var first = await evaluator.EvaluateAsync(owner, TimeSpan.FromHours(1), token);
            Assert.AreEqual(LinuxEvaluationStatus.Ready, first.Status);
            Assert.AreEqual(MatchState.Affected, first.Findings!.Single().State);
            var failing = new LinuxAssessmentWriter(new Factory(options), new OwnerFence(true), TimeProvider.System, new FailingNotificationWriter());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WriteAsync(owner, first, TimeSpan.FromHours(1), token));
            Assert.AreEqual(0, await db.VulnerabilityRuns.CountAsync(token));
            Assert.AreEqual(0, await db.VulnerabilityFindings.CountAsync(token));
            Assert.AreEqual(0, await db.VulnerabilityEvaluations.CountAsync(token));
            Assert.AreEqual(0, await db.VulnerabilityCoverage.CountAsync(token));
            Assert.AreEqual(LinuxWriteResult.Written, await writer.WriteAsync(owner, first, TimeSpan.FromHours(1), token));
            Assert.AreEqual(LinuxWriteResult.AlreadyWritten, await writer.WriteAsync(owner, first, TimeSpan.FromHours(1), token));
            var unknown = first with { RunId = Guid.CreateVersion7(), Findings = first.Findings!.Select(x => x with { State = MatchState.Unknown }).ToArray() };
            Assert.AreEqual(LinuxWriteResult.Written, await writer.WriteAsync(owner, unknown, TimeSpan.FromHours(1), token));
            var sameAffected = first with { RunId = Guid.CreateVersion7() };
            Assert.AreEqual(LinuxWriteResult.Written, await writer.WriteAsync(owner, sameAffected, TimeSpan.FromHours(1), token));
            await using (var operations = new OperationsDbContext(operationOptions))
                Assert.AreEqual(1, await operations.Set<Notification>().CountAsync(token));
            var current = await new VulnerabilityCurrent(new Factory(options), TimeProvider.System)
                .GetAsync(owner, 1, TimeSpan.FromHours(1), token, debian: true);
            Assert.AreEqual(VulnerabilityCoverageStatus.Current, current.Status);
            Assert.AreEqual("debian-security", current.SourceProvider);
            Assert.AreEqual("CVE-2025-1234", current.Items.Single().AdvisoryId);
            var findingId = current.Items.Single().Id;
            var scheduled = new LinuxReassessment(new Factory(options), new OwnerResolver(owner), new OwnerFence(true), TimeProvider.System, NullLogger<LinuxReassessment>.Instance, new NotificationWriter());
            await PublishPackage("2.0-1", 2);
            var fixedAssessment = await evaluator.EvaluateAsync(owner, TimeSpan.FromHours(1), token);
            Assert.AreEqual(MatchState.Fixed, fixedAssessment.Findings!.Single().State);
            await scheduled.RunAsync(token);
            var afterFix = await new VulnerabilityCurrent(new Factory(options), TimeProvider.System)
                .GetAsync(owner, 1, TimeSpan.FromHours(1), token, debian: true);
            Assert.AreEqual((int)MatchState.Fixed, afterFix.Items.Single().State);
            await using (var operations = new OperationsDbContext(operationOptions))
                Assert.AreEqual(2, await operations.Set<Notification>().CountAsync(token));
            await PublishPackage("1.0-1", 3);
            var resurfaced = await evaluator.EvaluateAsync(owner, TimeSpan.FromHours(1), token);
            Assert.AreEqual(MatchState.Affected, resurfaced.Findings!.Single().State);
            await scheduled.RunAsync(token);
            var monitoring = new VulnerabilityMonitoring(new Factory(options), new DeviceSource(owner), new OwnerResolver(owner), TimeProvider.System,
                new KevCatalog(new Factory(options), TimeProvider.System), NullLogger<VulnerabilityMonitoring>.Instance);
            var afterReturn = await monitoring.GetCurrentAsync(owner.DeviceId, 1, null, null, token);
            Assert.IsNotNull(afterReturn);
            Assert.AreEqual("debian-security", afterReturn.SourceProvider);
            Assert.AreEqual((int)MatchState.Affected, afterReturn.Items.Single().State);
            await using (var operations = new OperationsDbContext(operationOptions))
                Assert.AreEqual(3, await operations.Set<Notification>().CountAsync(token));
            var history = await new VulnerabilityHistory(new Factory(options)).GetAsync(owner.DeviceId, findingId, now.AddMinutes(-1), now.AddMinutes(1), null, token);
            Assert.IsNotNull(history);
            Assert.HasCount(5, history.Items);
            CollectionAssert.AreEquivalent(new[] { (int)MatchState.Affected, (int)MatchState.Unknown, (int)MatchState.Affected, (int)MatchState.Fixed, (int)MatchState.Affected }, history.Items.Select(x => x.State).ToArray());

            Task PublishPackage(string version, long generation) => Publish(InventorySource.LinuxDpkg, InventoryChunkCodec.DpkgSourceSchemaVersion,
                new InventoryEntry("openssl:amd64", "openssl", version, "amd64", SourcePackageName: "openssl", SourcePackageVersion: version), generation);
            async Task Publish(InventorySource kind, int schema, InventoryEntry item, long generation)
            {
                InventoryEntry[] items = [item];
                var measured = InventoryDigest.MeasureChunk(items);
                var chunk = new InventoryChunk(schema, Guid.CreateVersion7(), now, Guid.CreateVersion7(), generation, kind, now,
                    InventoryOutcome.Complete, 0, 1, 1, measured.Bytes, InventoryDigest.Combine([measured.Digest]), items);
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var staged = await InventoryStaging.StoreAsync(db, owner, chunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
                await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
                await transaction.CommitAsync(token);
                db.ChangeTracker.Clear();
            }
        }, context.CancellationToken);

    [TestMethod]
    public Task Load_RequiresCurrentVerifiedInventoryForResolvedAgent() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            await Publish(InventorySource.HostSystem, InventoryChunkCodec.SchemaVersion,
                new InventoryEntry("host", "host", "6.8", "x64", OperatingSystem: "Linux", ProcessArchitecture: "x64",
                    Runtime: ".NET 10", AvailableProcessorCount: 2, OperatingSystemFamily: "Linux", DistributionId: "ubuntu", DistributionVersion: "24.04"));
            await Publish(InventorySource.LinuxDpkg, InventoryChunkCodec.DpkgSourceSchemaVersion,
                new InventoryEntry("openssl:amd64", "openssl", "1.2-1", "amd64", SourcePackageName: "openssl", SourcePackageVersion: "1.2-1"),
                secondItem: new InventoryEntry("other:amd64", "other", "1.0-1", "amd64", SourcePackageName: "other", SourcePackageVersion: "1.0-1"),
                thirdItem: new InventoryEntry("libssl:amd64", "libssl", "2.0-1", "amd64", SourcePackageName: "openssl", SourcePackageVersion: "2.0-1"));
            var result = await LinuxInventory.LoadAsync(db, owner, now, TimeSpan.FromHours(1), token);
            Assert.AreEqual(LinuxInventoryStatus.Ready, result.Status);
            Assert.AreEqual("Ubuntu:24.04:LTS", result.Ecosystem);
            Assert.AreEqual("openssl", result.Packages!.Single(x => x.Key == "openssl:amd64").SourcePackageName);
            var source = new IntelligenceSource { Key = UbuntuRevisionStage.SourceKey, Enabled = true, LastSuccessfulCheckAt = now };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "test", RetrievedAt = now,
                Digest = new byte[32], Artifact = [1], SchemaVersion = UbuntuRevisionStage.SchemaVersion,
                MappingVersion = UbuntuRevisionStage.MappingVersion, LicenseEvidence = UbuntuRevisionStage.LicenseEvidence, RecordCount = 1 };
            source.LastObservedRevisionId = revision.Id;
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            var raw = Encoding.UTF8.GetBytes("""
                {"id":"UBUNTU-CVE-2025-0012","modified":"2025-01-01T00:00:00Z","severity":[{"type":"Ubuntu","score":"high"}],"affected":[{"package":{"ecosystem":"Ubuntu:24.04:LTS","name":"openssl"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"2.0-1"}]}]},{"package":{"ecosystem":"Ubuntu:24.04:LTS","name":"openssl"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"3.0-1"},{"fixed":"4.0-1"}]}]}]}
                """);
            var record = UbuntuOsvReader.Read(raw, 1024);
            var document = await UbuntuOsvDocument.CreateAsync(record, raw, token);
            var writer = await UbuntuRevisionWriter.OpenAsync(new Factory(options), revision.Id, revision.MappingVersion, token);
            await writer.WriteAsync([new(record.Id, document)], token);
            await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Normalized, true), token);
            db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = now });
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            var evaluation = new UbuntuPackageEvaluation(new Factory(options), TimeProvider.System);
            var finding = await evaluation.EvaluateAsync(owner, TimeSpan.FromHours(1), token);
            Assert.AreEqual(LinuxEvaluationStatus.Ready, finding.Status);
            Assert.AreEqual(MatchState.Affected, finding.Findings!.Single(x => x.PackageKey == "openssl:amd64").State);
            Assert.AreEqual(UbuntuFixStatus.Available, finding.Findings!.Single(x => x.PackageKey == "openssl:amd64").FixStatus);
            Assert.AreEqual("2.0-1", finding.Findings!.Single(x => x.PackageKey == "openssl:amd64").FixedVersion);
            Assert.AreEqual("high", finding.Findings!.Single(x => x.PackageKey == "openssl:amd64").UbuntuPriority);
            Assert.AreEqual(PackageOrigin.AssumedDistribution, finding.Findings!.Single(x => x.PackageKey == "openssl:amd64").Origin);
            Assert.AreEqual(MatchState.Fixed, finding.Findings!.Single(x => x.PackageKey == "libssl:amd64").State);
            Assert.AreEqual(MatchState.Unknown, finding.Findings!.Single(x => x.PackageKey == "other:amd64").State);
            Assert.IsNull(finding.Findings!.Single(x => x.PackageKey == "other:amd64").AdvisoryId);
            Assert.AreEqual(LinuxFindingReason.NoAdvisoryEvidence, finding.Findings!.Single(x => x.PackageKey == "other:amd64").Reason);
            var assessmentWriter = new LinuxAssessmentWriter(new Factory(options), new OwnerFence(true), TimeProvider.System);
            Assert.AreEqual(LinuxWriteResult.Written, await assessmentWriter.WriteAsync(owner, finding, TimeSpan.FromHours(1), token));
            Assert.AreEqual(LinuxWriteResult.AlreadyWritten, await assessmentWriter.WriteAsync(owner, finding, TimeSpan.FromHours(1), token));
            var nextRun = finding with { RunId = Guid.CreateVersion7() };
            Assert.AreEqual(LinuxWriteResult.Written, await assessmentWriter.WriteAsync(owner, nextRun, TimeSpan.FromHours(1), token));
            Assert.AreEqual(LinuxWriteResult.AlreadyWritten, await assessmentWriter.WriteAsync(owner, finding, TimeSpan.FromHours(1), token));
            var modified = finding.Findings!.Select(x => x with { State = MatchState.Unknown }).ToArray();
            Assert.AreEqual(LinuxWriteResult.Conflict, await assessmentWriter.WriteAsync(owner, finding with { Findings = modified }, TimeSpan.FromHours(1), token));
            var changedPriority = finding.Findings!.Select(x => x.AdvisoryId is null ? x : x with { UbuntuPriority = "low" }).ToArray();
            Assert.AreEqual(LinuxWriteResult.Conflict, await assessmentWriter.WriteAsync(owner, finding with { Findings = changedPriority }, TimeSpan.FromHours(1), token));
            Assert.AreEqual(2, await db.VulnerabilityFindings.CountAsync(token));
            Assert.AreEqual(4, await db.VulnerabilityEvaluations.CountAsync(token));
            var migrations = db.Database.GetMigrations().ToArray();
            var priorityMigration = Array.FindIndex(migrations, x => x.EndsWith("_UbuntuPriority", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, priorityMigration);
            await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[priorityMigration - 1], token));
            Assert.AreEqual(4, await db.VulnerabilityEvaluations.CountAsync(x => x.UbuntuPriority == "high", token));
            var gap = await db.VulnerabilityUnknownPackages.AsNoTracking().SingleAsync(x => x.RunId == finding.RunId, token);
            Assert.AreEqual("other:amd64", gap.PackageKey);
            Assert.AreEqual((int)LinuxFindingReason.NoAdvisoryEvidence, gap.Reason);
            Assert.AreEqual("other", gap.SourcePackage);
            Assert.AreEqual("1.0-1", gap.BinaryVersion);
            var runEvidence = await db.VulnerabilityRuns.AsNoTracking().SingleAsync(x => x.RunId == finding.RunId, token);
            Assert.AreEqual(revision.Id, runEvidence.RevisionId);
            Assert.AreEqual(owner.AgentId, runEvidence.AgentId);
            Assert.AreEqual(finding.HostSnapshotId, runEvidence.HostSnapshotId);
            Assert.AreEqual(finding.PackageSnapshotId, runEvidence.PackageSnapshotId);
            Assert.AreEqual(LinuxAssessmentWriter.MatcherVersion, runEvidence.MatcherVersion);
            Assert.AreEqual("Ubuntu:24.04:LTS", runEvidence.Ecosystem);
            var coverage = await db.VulnerabilityCoverage.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(3, coverage.PackageCount);
            Assert.AreEqual(1, coverage.UnknownPackageCount);
            var currentQuery = new VulnerabilityCurrent(new Factory(options), TimeProvider.System);
            var current = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token);
            Assert.AreEqual(VulnerabilityCoverageStatus.Current, current.Status);
            Assert.HasCount(2, current.Items);
            var openssl = current.Items.Single(x => x.PackageKey == "openssl:amd64");
            Assert.AreEqual((int)UbuntuFixStatus.Available, openssl.FixStatus);
            Assert.AreEqual("2.0-1", openssl.FixedVersion);
            Assert.AreEqual("high", openssl.UbuntuPriority);
            var history = await new VulnerabilityHistory(new Factory(options)).GetAsync(owner.DeviceId, openssl.Id,
                now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), null, token);
            Assert.IsNotNull(history);
            Assert.IsTrue(history.Items.Any(x => x.FixStatus == (int)UbuntuFixStatus.Available && x.FixedVersion == "2.0-1"));
            Assert.IsTrue(history.Items.All(x => x.UbuntuPriority == "high"));
            Assert.AreEqual((int)PackageOrigin.AssumedDistribution, current.Items[0].Origin);
            Assert.AreEqual(1, current.UnknownPackageCount);
            Assert.IsNotNull(current.CoverageGaps);
            Assert.AreEqual("other:amd64", current.CoverageGaps.Single().PackageKey);
            var epssArtifact = new byte[18];
            var epssSource = new IntelligenceSource { Key = EpssSnapshotStage.SourceKey, Enabled = true, LastSuccessfulCheckAt = now };
            var epssRevision = new IntelligenceRevision { SourceId = epssSource.Id, UpstreamVersion = "v1", RetrievedAt = now, ReleasedAt = now,
                Digest = SHA256.HashData(epssArtifact), Artifact = epssArtifact, SchemaVersion = EpssSnapshotStage.Schema,
                MappingVersion = EpssSnapshotStage.Mapping, LicenseEvidence = EpssRefresh.LicenseEvidence, RecordCount = 1, Normalized = true };
            var kevSource = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = now };
            var kevRevision = new IntelligenceRevision { SourceId = kevSource.Id, UpstreamVersion = "v1", RetrievedAt = now,
                Digest = SHA256.HashData([1]), Artifact = [1], SchemaVersion = KevCatalogValidator.SchemaRevision,
                MappingVersion = KevUpdate.MappingVersion, LicenseEvidence = KevUpdate.LicenseEvidence, RecordCount = 1, Normalized = true };
            db.IntelligenceSources.AddRange(epssSource, kevSource);
            db.IntelligenceRevisions.AddRange(epssRevision, kevRevision);
            db.IntelligenceActivations.AddRange(new IntelligenceActivation { SourceId = epssSource.Id, RevisionId = epssRevision.Id, ActivatedAt = now },
                new IntelligenceActivation { SourceId = kevSource.Id, RevisionId = kevRevision.Id, ActivatedAt = now });
            db.IntelligenceActivationEvents.Add(new IntelligenceActivationEvent { SourceId = kevSource.Id, RevisionId = kevRevision.Id, Scheduled = true, OccurredAt = now });
            db.EpssScores.Add(new EpssScoreRecord { RevisionId = epssRevision.Id, Cve = "CVE-2025-0012", Probability = 0.85m, Percentile = 0.99m });
            db.KevRecords.Add(new KevRecord { RevisionId = kevRevision.Id, CveId = "CVE-2025-0012", VendorProject = "test", Product = "test", VulnerabilityName = "test", Description = "test", RequiredAction = "test" });
            await db.SaveChangesAsync(token);
            var kevSnapshot = await new KevCatalog(new Factory(options), TimeProvider.System).GetActiveAsync(token);
            var ranked = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot);
            Assert.AreEqual("Ready", ranked.KevStatus);
            Assert.AreEqual("Ready", ranked.EpssStatus);
            Assert.AreEqual("openssl:amd64", ranked.Items[0].PackageKey);
            Assert.IsTrue(ranked.Items[0].KnownExploited);
            Assert.AreEqual(0.85m, ranked.Items[0].EpssProbability);
            var expiresDuringQuery = await new VulnerabilityCurrent(new Factory(options), new QueryExpiryClock(now.AddSeconds(1)))
                .GetAsync(owner, 1, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot);
            Assert.IsTrue(expiresDuringQuery.RankingChanged);
            Assert.IsEmpty(expiresDuringQuery.Items);
            var addedFindings = new List<Guid>();
            for (var i = 0; i < 52; i++)
            {
                var added = new Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities.VulnerabilityFinding
                {
                    DeviceId = owner.DeviceId, SourceId = source.Id, IdentityHash = SHA256.HashData(Encoding.UTF8.GetBytes($"rank-{i}")),
                    PackageKey = $"rank-{i:D2}", AdvisoryId = $"UBUNTU-CVE-2025-{i + 1000}", CurrentState = 0,
                    FirstEvaluatedAt = now, LastEvaluatedAt = now, LastPackageSnapshotId = coverage.PackageSnapshotId,
                    LastRevisionId = revision.Id, LastRunId = coverage.RunId
                };
                addedFindings.Add(added.Id);
                db.VulnerabilityFindings.Add(added);
                db.VulnerabilityEvaluations.Add(new Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities.VulnerabilityEvaluation
                {
                    FindingId = added.Id, RunId = coverage.RunId, SourceId = source.Id, RevisionId = revision.Id, AgentId = owner.AgentId,
                    DeviceGeneration = owner.DeviceGeneration, HostSnapshotId = coverage.HostSnapshotId, PackageSnapshotId = coverage.PackageSnapshotId,
                    EvaluatedAt = now, MatcherVersion = LinuxAssessmentWriter.MatcherVersion, SourcePackage = "test", SourceVersion = "1",
                    BinaryVersion = "1", Ecosystem = "Ubuntu:24.04:LTS", Architecture = "amd64", State = 0
                });
            }
            db.EpssScores.Add(new EpssScoreRecord { RevisionId = epssRevision.Id, Cve = "CVE-2025-1051", Probability = 0.99m, Percentile = 0.99m });
            db.KevRecords.Add(new KevRecord { RevisionId = kevRevision.Id, CveId = "CVE-2025-1050", VendorProject = "test", Product = "test", VulnerabilityName = "test", Description = "test", RequiredAction = "test" });
            await db.SaveChangesAsync(token);
            await db.IntelligenceRevisions.Where(x => x.Id == epssRevision.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.RecordCount, 2), token);
            var firstRiskPage = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot);
            var secondRiskPage = await currentQuery.GetAsync(owner, 2, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot, rankingVersion: firstRiskPage.RankingVersion);
            Assert.IsTrue(firstRiskPage.HasMore);
            Assert.AreEqual("openssl:amd64", firstRiskPage.Items[0].PackageKey);
            Assert.AreEqual("rank-50", firstRiskPage.Items[1].PackageKey);
            Assert.AreEqual("rank-51", firstRiskPage.Items[2].PackageKey);
            Assert.IsFalse(firstRiskPage.Items.Select(x => x.Id).Intersect(secondRiskPage.Items.Select(x => x.Id)).Any());
            await db.VulnerabilityEvaluations.Where(x => addedFindings.Contains(x.FindingId)).ExecuteDeleteAsync(token);
            await db.VulnerabilityFindings.Where(x => addedFindings.Contains(x.Id)).ExecuteDeleteAsync(token);
            await db.EpssScores.Where(x => x.RevisionId == epssRevision.Id && x.Cve == "CVE-2025-1051").ExecuteDeleteAsync(token);
            await db.IntelligenceRevisions.Where(x => x.Id == epssRevision.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.RecordCount, 1), token);
            var replacement = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "replacement", RetrievedAt = now,
                Digest = SHA256.HashData([2]), Artifact = [2], SchemaVersion = UbuntuRevisionStage.SchemaVersion,
                MappingVersion = UbuntuRevisionStage.MappingVersion, LicenseEvidence = UbuntuRevisionStage.LicenseEvidence, RecordCount = 0 };
            db.IntelligenceRevisions.Add(replacement);
            await db.SaveChangesAsync(token);
            await db.IntelligenceActivations.Where(x => x.SourceId == source.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.RevisionId, replacement.Id), token);
            await db.IntelligenceActivations.Where(x => x.SourceId == source.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.RevisionId, revision.Id), token);
            var changedActivation = await currentQuery.GetAsync(owner, 2, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot, rankingVersion: ranked.RankingVersion);
            Assert.IsTrue(changedActivation.RankingChanged);
            finding = finding with { ActivationVersion = await db.IntelligenceActivations.AsNoTracking().Where(x => x.SourceId == source.Id).Select(x => x.Version).SingleAsync(token) };
            ranked = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot);
            var downgraded = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, includeRisk: false);
            Assert.AreEqual("Unavailable", downgraded.EpssStatus);
            Assert.IsTrue(downgraded.Items.All(x => x.EpssProbability is null && x.KnownExploited is null));
            var changedMode = await currentQuery.GetAsync(owner, 2, TimeSpan.FromHours(1), token, rankingVersion: ranked.RankingVersion, includeRisk: false);
            Assert.IsTrue(changedMode.RankingChanged);
            await db.IntelligenceSources.Where(x => x.Id == epssSource.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false), token);
            var changedPage = await currentQuery.GetAsync(owner, 2, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot, rankingVersion: ranked.RankingVersion);
            Assert.IsTrue(changedPage.RankingChanged);
            Assert.IsEmpty(changedPage.Items);
            var withoutEpss = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, kevSnapshot: kevSnapshot);
            Assert.AreEqual("Unavailable", withoutEpss.EpssStatus);
            Assert.IsTrue(withoutEpss.Items.All(x => x.EpssProbability is null));
            Assert.AreEqual((int)LinuxFindingReason.NoAdvisoryEvidence, current.CoverageGaps.Single().Reason);
            Assert.AreEqual("1.0-1", current.CoverageGaps.Single().BinaryVersion);
            var unranked = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token);
            Assert.IsEmpty((await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, gapPage: 2, rankingVersion: unranked.RankingVersion)).CoverageGaps!);
            await Assert.ThrowsAsync<ArgumentException>(() => currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, gapPage: 0));
            db.VulnerabilityUnknownPackages.AddRange(Enumerable.Range(0, 51).Select(i => new Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities.VulnerabilityUnknownPackage
                { RunId = coverage.RunId, PackageKey = $"gap-{i:D2}", Reason = (int)LinuxFindingReason.NoAdvisoryEvidence }));
            await db.SaveChangesAsync(token); db.ChangeTracker.Clear();
            var gapFirst = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token);
            Assert.HasCount(50, gapFirst.CoverageGaps!);
            Assert.IsTrue(gapFirst.GapsHaveMore);
            var gapSecond = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, gapPage: 2, rankingVersion: gapFirst.RankingVersion);
            Assert.HasCount(2, gapSecond.CoverageGaps!);
            Assert.IsFalse(gapSecond.GapsHaveMore);
            Assert.IsFalse(gapFirst.CoverageGaps!.Select(x => x.PackageKey).Intersect(gapSecond.CoverageGaps!.Select(x => x.PackageKey)).Any());
            await db.VulnerabilityUnknownPackages.Where(x => x.RunId == coverage.RunId && x.PackageKey.StartsWith("gap-")).ExecuteDeleteAsync(token);
            var filtered = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, "OPENSSL", 0);
            Assert.HasCount(1, filtered.Items);
            Assert.AreEqual("openssl:amd64", filtered.Items[0].PackageKey);
            Assert.HasCount(0, (await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, "%", null)).Items);
            await Assert.ThrowsAsync<ArgumentException>(() => currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token, null, 9));
            var reassessment = new LinuxReassessment(new Factory(options), new OwnerResolver(owner), new OwnerFence(true), TimeProvider.System, NullLogger<LinuxReassessment>.Instance);
            var existingRunCount = await db.VulnerabilityRuns.CountAsync(token);
            await reassessment.RunAsync(token);
            Assert.AreEqual(existingRunCount, await db.VulnerabilityRuns.CountAsync(token));
            var farPage = await currentQuery.GetAsync(owner, 5000, TimeSpan.FromHours(1), token, rankingVersion: gapFirst.RankingVersion);
            Assert.IsEmpty(farPage.Items);
            await using (var lockDb = new SecurityAnalysisDbContext(options))
            await using (var lockTransaction = await lockDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
            using (var queryStop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                await lockDb.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {source.Id}").SingleAsync(token);
                var blockedQuery = currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), queryStop.Token);
                var failures = new List<Exception>();
                try
                {
                    await Task.Delay(150, token);
                    Assert.IsFalse(blockedQuery.IsCompleted);
                    await lockTransaction.CommitAsync(token);
                    Assert.AreEqual(VulnerabilityCoverageStatus.Current, (await blockedQuery).Status);
                }
                catch (Exception error) { failures.Add(error); }
                finally
                {
                    try { await queryStop.CancelAsync(); } catch (Exception error) { failures.Add(error); }
                    try { await lockTransaction.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
                    try { await blockedQuery; } catch (OperationCanceledException) { } catch (Exception error) { failures.Add(error); }
                }
                if (failures.Count > 0) throw new AggregateException(failures);
            }
            var firstFinding = finding.Findings!.First(x => x.AdvisoryId is not null);
            var largeBatch = Enumerable.Range(0, 129).Select(i => firstFinding with { AdvisoryId = $"UBUNTU-CVE-2025-{i:D4}" }).ToList();
            largeBatch.Add(largeBatch[0]);
            await Assert.ThrowsAsync<InvalidDataException>(() => assessmentWriter.WriteAsync(owner,
                finding with { RunId = Guid.CreateVersion7(), Findings = largeBatch }, TimeSpan.FromHours(1), token));
            Assert.AreEqual(2, await db.VulnerabilityFindings.CountAsync(token));
            Assert.AreEqual(4, await db.VulnerabilityEvaluations.CountAsync(token));
            largeBatch.RemoveAt(largeBatch.Count - 1);
            var bulk = finding with { RunId = Guid.CreateVersion7(), Findings = largeBatch };
            Assert.AreEqual(LinuxWriteResult.Written, await assessmentWriter.WriteAsync(owner, bulk, TimeSpan.FromHours(1), token));
            var bulkAgain = bulk with { RunId = Guid.CreateVersion7() };
            Assert.AreEqual(LinuxWriteResult.Written, await assessmentWriter.WriteAsync(owner, bulkAgain, TimeSpan.FromHours(1), token));
            Assert.AreEqual(130, await db.VulnerabilityFindings.CountAsync(token));
            Assert.AreEqual(262, await db.VulnerabilityEvaluations.CountAsync(token));
            var empty = finding with { RunId = Guid.CreateVersion7(), Findings = [] };
            Assert.AreEqual(LinuxWriteResult.Written, await assessmentWriter.WriteAsync(owner, empty, TimeSpan.FromHours(1), token));
            Assert.AreEqual(LinuxWriteResult.AlreadyWritten, await assessmentWriter.WriteAsync(owner, empty, TimeSpan.FromHours(1), token));
            var expiringClock = new MutableClock(DateTimeOffset.UtcNow);
            var expiringWriter = new LinuxAssessmentWriter(new Factory(options), new ExpiringOwnerFence(expiringClock), expiringClock);
            var expiring = finding with { RunId = Guid.CreateVersion7() };
            Assert.AreEqual(LinuxWriteResult.Changed, await expiringWriter.WriteAsync(owner, expiring, TimeSpan.FromHours(1), token));
            Assert.IsFalse(await db.VulnerabilityRuns.AnyAsync(x => x.RunId == expiring.RunId, token));
            Assert.AreEqual(262, await db.VulnerabilityEvaluations.CountAsync(token));
            await using (var fenceDb = new SecurityAnalysisDbContext(options))
            await using (var transaction = await fenceDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
            {
                Assert.IsTrue(await LinuxAssessmentFence.LockCurrentAsync(fenceDb, new OwnerFence(true), owner, finding, TimeProvider.System, TimeSpan.FromHours(1), token));
                Assert.IsFalse(await LinuxAssessmentFence.LockCurrentAsync(fenceDb, new OwnerFence(false), owner, finding, TimeProvider.System, TimeSpan.FromHours(1), token));
                var clock = new MutableClock(now);
                Assert.IsFalse(await LinuxAssessmentFence.LockCurrentAsync(fenceDb, new AdvancingOwnerFence(clock, TimeSpan.FromHours(2)), owner,
                    finding, clock, TimeSpan.FromHours(1), token));
                await transaction.CommitAsync(token);
            }
            var changedPackage = result.Packages!.Single(x => x.Key == "openssl:amd64") with { Version = "2.0-1", SourcePackageVersion = "2.0-1" };
            await Publish(InventorySource.LinuxDpkg, InventoryChunkCodec.DpkgSourceSchemaVersion, changedPackage, 2);
            var secondOwner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            await Publish(InventorySource.HostSystem, InventoryChunkCodec.SchemaVersion,
                new InventoryEntry("host", "host2", "6.8", "x64", OperatingSystem: "Linux", ProcessArchitecture: "x64",
                    Runtime: ".NET 10", AvailableProcessorCount: 2, OperatingSystemFamily: "Linux", DistributionId: "ubuntu", DistributionVersion: "24.04"), target: secondOwner);
            await Publish(InventorySource.LinuxDpkg, InventoryChunkCodec.DpkgSourceSchemaVersion, changedPackage, target: secondOwner);
            var isolated = new LinuxReassessment(new Factory(options), new FailingOwnerResolver(owner.DeviceId, secondOwner), new OwnerFence(true),
                TimeProvider.System, NullLogger<LinuxReassessment>.Instance);
            await isolated.RunAsync(token);
            Assert.IsTrue(await db.VulnerabilityCoverage.AnyAsync(x => x.DeviceId == secondOwner.DeviceId, token));
            await reassessment.RunAsync(token);
            var refreshed = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token);
            Assert.AreEqual(VulnerabilityCoverageStatus.Current, refreshed.Status);
            Assert.AreEqual((int)MatchState.Fixed, refreshed.Items.Single().State);
            await Publish(InventorySource.LinuxDpkg, InventoryChunkCodec.DpkgSourceSchemaVersion,
                changedPackage with { Version = "1.2-1", SourcePackageVersion = "1.2-1" }, 3);
            await reassessment.RunAsync(token);
            var reappeared = await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token);
            Assert.AreEqual((int)MatchState.Affected, reappeared.Items.Single().State);
            Assert.AreEqual(openssl.Id, reappeared.Items.Single().Id);
            var timeline = await new VulnerabilityHistory(new Factory(options)).GetAsync(owner.DeviceId, openssl.Id,
                now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), null, token);
            Assert.IsNotNull(timeline);
            Assert.AreEqual((int)MatchState.Affected, timeline.Items[0].State);
            Assert.AreEqual((int)MatchState.Fixed, timeline.Items[1].State);
            Assert.IsTrue(timeline.Items.Skip(2).Any(x => x.State == (int)MatchState.Affected));
            var afterRefreshCount = await db.VulnerabilityEvaluations.CountAsync(token);
            var newer = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "newer", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = Enumerable.Repeat((byte)9, 32).ToArray(), Artifact = [2], SchemaVersion = UbuntuRevisionStage.SchemaVersion,
                MappingVersion = UbuntuRevisionStage.MappingVersion, LicenseEvidence = UbuntuRevisionStage.LicenseEvidence, RecordCount = 1, Normalized = true };
            db.IntelligenceRevisions.Add(newer);
            await db.SaveChangesAsync(token);
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x
                .SetProperty(s => s.LastObservedRevisionId, newer.Id).SetProperty(s => s.LastSuccessfulCheckAt, DateTimeOffset.UtcNow), token);
            Assert.AreEqual(VulnerabilityCoverageStatus.Stale, (await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token)).Status);
            Assert.AreEqual(LinuxEvaluationStatus.SourceUnavailable, (await evaluation.EvaluateAsync(owner, TimeSpan.FromHours(1), token)).Status);
            Assert.AreEqual(LinuxWriteResult.Changed, await assessmentWriter.WriteAsync(owner, finding with { RunId = Guid.CreateVersion7() }, TimeSpan.FromHours(1), token));
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false), token);
            Assert.AreEqual(VulnerabilityCoverageStatus.Stale, (await currentQuery.GetAsync(owner, 1, TimeSpan.FromHours(1), token)).Status);
            Assert.AreEqual(LinuxWriteResult.Changed, await assessmentWriter.WriteAsync(owner, finding with { RunId = Guid.CreateVersion7() }, TimeSpan.FromHours(1), token));
            Assert.AreEqual(afterRefreshCount, await db.VulnerabilityEvaluations.CountAsync(token));
            Assert.AreEqual(LinuxEvaluationStatus.SourceUnavailable,
                (await evaluation.EvaluateAsync(owner, TimeSpan.FromHours(1), token)).Status);
            await using (var fenceDb = new SecurityAnalysisDbContext(options))
            await using (var transaction = await fenceDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
            {
                Assert.IsFalse(await LinuxAssessmentFence.LockCurrentAsync(fenceDb, new OwnerFence(true), owner, finding, TimeProvider.System, TimeSpan.FromHours(1), token));
                await transaction.CommitAsync(token);
            }
            Assert.AreEqual(LinuxInventoryStatus.Stale, (await LinuxInventory.LoadAsync(db, owner, now.AddHours(2), TimeSpan.FromHours(1), token)).Status);
            Assert.AreEqual(LinuxInventoryStatus.Missing, (await LinuxInventory.LoadAsync(db, owner with { DeviceGeneration = 2 }, now, TimeSpan.FromHours(1), token)).Status);
            var package = result.Packages!.Single(x => x.Key == "openssl:amd64");
            await Publish(InventorySource.LinuxDpkg, InventoryChunkCodec.DpkgSourceSchemaVersion, package, 4, InventoryOutcome.Partial);
            Assert.AreEqual(LinuxInventoryStatus.Incomplete, (await LinuxInventory.LoadAsync(db, owner, now, TimeSpan.FromHours(1), token)).Status);
            await Publish(InventorySource.LinuxDpkg, InventoryChunkCodec.DpkgSourceSchemaVersion, package, 5);
            await db.InventoryChunks.Where(x => x.AgentId == owner.AgentId && x.Source == InventorySource.LinuxDpkg)
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.Payload, new byte[] { 1 }), token);
            Assert.AreEqual(LinuxInventoryStatus.Invalid, (await LinuxInventory.LoadAsync(db, owner, now, TimeSpan.FromHours(1), token)).Status);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE [security].[EpssScores]", token);
            var monitoring = new VulnerabilityMonitoring(new Factory(options), new DeviceSource(owner), new OwnerResolver(owner), TimeProvider.System,
                new KevCatalog(new Factory(options), TimeProvider.System), NullLogger<VulnerabilityMonitoring>.Instance);
            var afterRiskFailure = await monitoring.GetCurrentAsync(owner.DeviceId, 1, null, null, token);
            Assert.IsNotNull(afterRiskFailure);
            Assert.AreEqual("Unavailable", afterRiskFailure.EpssStatus);
            Assert.IsTrue(afterRiskFailure.Items.All(x => x.EpssProbability is null && x.KnownExploited is null));
            await Publish(InventorySource.HostSystem, InventoryChunkCodec.SchemaVersion,
                new InventoryEntry("host", "host", "6.1", "x64", OperatingSystem: "Linux", ProcessArchitecture: "x64",
                    Runtime: ".NET 10", AvailableProcessorCount: 2, OperatingSystemFamily: "Linux", DistributionId: "fedora", DistributionVersion: "41"), 2);
            var unsupported = await monitoring.GetCurrentAsync(owner.DeviceId, 1, null, null, token);
            Assert.AreEqual(VulnerabilityCoverageStatus.Unsupported, unsupported!.Status);
            Assert.IsEmpty(unsupported.Items);

            async Task Publish(InventorySource source, int schema, InventoryEntry item, long generation = 1, InventoryOutcome outcome = InventoryOutcome.Complete, InventoryEntry? secondItem = null, InventoryEntry? thirdItem = null, InventoryOwner? target = null)
            {
                InventoryEntry[] items = (secondItem, thirdItem) switch { (not null, not null) => [item, secondItem, thirdItem],
                    (not null, null) => [item, secondItem], _ => [item] };
                var measured = InventoryDigest.MeasureChunk(items);
                var chunk = new InventoryChunk(schema, Guid.CreateVersion7(), now, Guid.CreateVersion7(), generation, source, now,
                    outcome, 0, 1, items.Length, measured.Bytes, InventoryDigest.Combine([measured.Digest]), items);
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var staged = await InventoryStaging.StoreAsync(db, target ?? owner, chunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
                await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
                await transaction.CommitAsync(token);
                db.ChangeTracker.Clear();
            }
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class OwnerFence(bool current) : IAgentOwnerFence
    {
        public Task<bool> LockCurrentAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner, CancellationToken token) => Task.FromResult(current);
    }

    private sealed class OwnerResolver(InventoryOwner owner) : IAgentOwnerResolver
    {
        public Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token) => Task.FromResult<InventoryOwner?>(deviceId == owner.DeviceId ? owner : null);
    }

    private sealed class DeviceSource(InventoryOwner owner) : IDeviceAgentSource
    {
        public Task<DeviceAgentState?> GetAsync(Guid deviceId, CancellationToken token) =>
            Task.FromResult<DeviceAgentState?>(deviceId == owner.DeviceId ? new(owner.DeviceId, owner.DeviceGeneration, true) : null);
        public Task<bool> IsCurrentAsync(Guid deviceId, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            Task.FromResult(deviceId == owner.DeviceId && generation == owner.DeviceGeneration);
    }

    private sealed class QueryExpiryClock(DateTimeOffset start) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref calls) >= 3 ? start.AddDays(4) : start;
    }

    private sealed class FailingOwnerResolver(Guid failingDevice, InventoryOwner other) : IAgentOwnerResolver
    {
        public Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token) => deviceId == failingDevice
            ? throw new InvalidDataException("Synthetic device failure.") : Task.FromResult<InventoryOwner?>(deviceId == other.DeviceId ? other : null);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdvancingOwnerFence(MutableClock clock, TimeSpan elapsed) : IAgentOwnerFence
    {
        public Task<bool> LockCurrentAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner, CancellationToken token)
        {
            clock.Now += elapsed;
            return Task.FromResult(true);
        }
    }

    private sealed class ExpiringOwnerFence(MutableClock clock) : IAgentOwnerFence
    {
        private int calls;
        public Task<bool> LockCurrentAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner, CancellationToken token)
        {
            if (++calls == 2) clock.Now += TimeSpan.FromHours(2);
            return Task.FromResult(true);
        }
    }
}
