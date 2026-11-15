using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using System.Net;
using System.Text;
using MsrcStoredDocument = Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence.MsrcDocument;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MsrcDocumentSqlTests(TestContext context)
{
    [TestMethod]
    public Task ProductLookup_PagesDistinctCvesAndRejectsChangedSource() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = MsrcSnapshotStage.SourceKey, Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            using var handler = new MsrcHandler { ExtraCves = 205, ScopedExtraCves = true, AddSecond = true };
            using var http = new HttpClient(handler);
            var factory = new Factory(options);
            var staged = await new MsrcSnapshotStage(factory, TimeProvider.System).StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Staged, staged.Status);
            Assert.AreEqual(IntelligencePublicationResult.Published, await new IntelligencePublication(factory, new OperationAuditWriter(), TimeProvider.System)
                .ActivateScheduledAsync(source.Id, staged.RevisionId!.Value, null, "", token));
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.LastObservedRevisionId, staged.RevisionId), token);
            var lookup = new MsrcProductLookup(factory, TimeProvider.System);
            const string product = "Windows Server 2022 (Server Core installation)";
            var first = await lookup.FindAsync(source.Id, staged.RevisionId.Value, product, null, token);
            Assert.AreEqual(MsrcCandidateStatus.Ready, first.Status);
            Assert.IsTrue(first.HasMore);
            Assert.IsTrue(first.CoverageComplete);
            Assert.IsNotNull(first.NextCve);
            Assert.AreEqual(MsrcCandidateStatus.Changed,
                (await lookup.FindAsync(source.Id, staged.RevisionId.Value, "Windows Server 2022", first.NextCve, token, first)).Status);
            Assert.AreEqual(MsrcCandidateStatus.Changed,
                (await lookup.FindAsync(source.Id, staged.RevisionId.Value, product, "CVE-2025-9999", token, first)).Status);
            var second = await lookup.FindAsync(source.Id, staged.RevisionId.Value, product, first.NextCve, token, first);
            Assert.AreEqual(MsrcCandidateStatus.Ready, second.Status);
            Assert.IsFalse(second.HasMore);
            var firstCves = first.Items.Select(x => x.Advisory.Cve).ToHashSet(StringComparer.Ordinal);
            var secondCves = second.Items.Select(x => x.Advisory.Cve).ToHashSet(StringComparer.Ordinal);
            Assert.HasCount(200, firstCves);
            Assert.HasCount(6, secondCves);
            Assert.IsFalse(firstCves.Overlaps(secondCves));
            await db.IntelligenceActivations.Where(x => x.SourceId == source.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.ActivatedAt, DateTimeOffset.UtcNow), token);
            var changed = await lookup.FindAsync(source.Id, staged.RevisionId.Value, product, first.NextCve, token, first);
            Assert.AreEqual(MsrcCandidateStatus.Changed, changed.Status);
        }, context.CancellationToken);

    [TestMethod]
    public Task Publication_RequiresConfirmationWhenCveAssociationsShrinkButDocumentCountDoesNot() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = MsrcSnapshotStage.SourceKey, Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token);
            using var handler = new MsrcHandler { ExtraCves = 10, ActionRequired = true };
            using var http = new HttpClient(handler);
            var factory = new Factory(options);
            var stage = new MsrcSnapshotStage(factory, TimeProvider.System);
            var first = await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Staged, first.Status);
            var publication = new IntelligencePublication(factory, new OperationAuditWriter(), TimeProvider.System);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateScheduledAsync(source.Id, first.RevisionId!.Value, null, "", token));
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.LastObservedRevisionId, first.RevisionId), token);
            db.ChangeTracker.Clear();
            source = await db.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token);
            var lookup = new MsrcCandidateLookup(factory, TimeProvider.System);
            var byProduct = new MsrcProductLookup(factory, TimeProvider.System);
            var productPage = await byProduct.FindAsync(source.Id, first.RevisionId.Value, "Windows Server 2022 (Server Core installation)", null, token);
            Assert.AreEqual(MsrcCandidateStatus.Ready, productPage.Status);
            Assert.IsTrue(productPage.Items.Any(x => x.Advisory.Cve == "CVE-2025-1234"));
            Assert.IsFalse(productPage.CoverageComplete); // Other CVEs in this fixture have no product scope.
            var indexedProducts = (await db.MsrcArtifacts.AsNoTracking().SingleAsync(token)).IndexedProductCount;
            await db.MsrcArtifacts.ExecuteUpdateAsync(x => x.SetProperty(a => a.IndexedProductCount, -1), token);
            Assert.AreEqual(MsrcCandidateStatus.Unavailable, (await byProduct.FindAsync(source.Id, first.RevisionId.Value, "Windows Server 2022 (Server Core installation)", null, token)).Status);
            await db.MsrcArtifacts.ExecuteUpdateAsync(x => x.SetProperty(a => a.IndexedProductCount, indexedProducts), token);
            Assert.AreEqual(MsrcCandidateStatus.Ready, (await byProduct.FindAsync(source.Id, first.RevisionId.Value, "Windows Server 2022 (Server Core installation)", null, token)).Status);
            var unrelatedPage = await byProduct.FindAsync(source.Id, first.RevisionId.Value, "Windows Server 2019", null, token);
            Assert.IsEmpty(unrelatedPage.Items);
            Assert.IsFalse(unrelatedPage.CoverageComplete);
            var candidates = await lookup.FindAsync(source.Id, first.RevisionId.Value, ["CVE-2025-1234"], token);
            Assert.AreEqual(MsrcCandidateStatus.Ready, candidates.Status);
            Assert.HasCount(1, candidates.Items);
            Assert.AreEqual("CVE-2025-1234", candidates.Items.Single().Advisory.Cve);
            Assert.IsTrue(await lookup.IsCurrentAsync(candidates, token));
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false), token);
            Assert.IsFalse(await lookup.IsCurrentAsync(candidates, token));
            Assert.AreEqual(MsrcCandidateStatus.Unavailable, (await byProduct.FindAsync(source.Id, first.RevisionId.Value, "Windows Server 2022 (Server Core installation)", null, token)).Status);
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, true), token);
            candidates = await lookup.FindAsync(source.Id, first.RevisionId.Value, ["CVE-2025-1234"], token);
            Assert.AreEqual(MsrcCandidateStatus.Ready, candidates.Status);
            await db.IntelligenceActivations.Where(x => x.SourceId == source.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.ActivatedAt, DateTimeOffset.UtcNow), token);
            Assert.IsFalse(await lookup.IsCurrentAsync(candidates, token));
            db.ChangeTracker.Clear();
            source = await db.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token);
            var missing = await lookup.FindAsync(source.Id, first.RevisionId.Value, ["CVE-2025-9999"], token);
            Assert.AreEqual(MsrcCandidateStatus.Ready, missing.Status);
            Assert.IsEmpty(missing.Items); // An absent candidate is not an unaffected result.
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var now = DateTimeOffset.UtcNow;
            var updateId = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var update = new InventoryEntry($"{updateId:D}:1", "security update", UpdateId: updateId, UpdateRevision: 1,
                UpdateInstalled: false, UpdateRebootRequired: false, UpdateCves: ["CVE-2025-1234", "CVE-2025-9999"],
                UpdateKbs: Enumerable.Range(0, 21).Select(i => $"{1234567 + i}").ToArray(), UpdateServerSelection: 0,
                UpdateServiceId: Guid.Empty, UpdateSupersededIds: ["22222222-2222-4222-8222-222222222222"]);
            var measured = InventoryDigest.MeasureChunk([update]);
            var chunk = new InventoryChunk(InventoryChunkCodec.WindowsUpdateRelationshipsSchemaVersion, Guid.CreateVersion7(), now, Guid.CreateVersion7(),
                1, InventorySource.WindowsUpdate, now, InventoryOutcome.Partial, 0, 1, 1, measured.Bytes, InventoryDigest.Combine([measured.Digest]), [update]);
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var staged = await InventoryStaging.StoreAsync(db, owner, chunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
                await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
                await transaction.CommitAsync(token);
            }
            db.ChangeTracker.Clear();
            var view = await new WindowsVulnerabilityEvidence(factory, TimeProvider.System, NullLogger.Instance).GetAsync(owner, 1, token);
            Assert.IsNotNull(view?.Windows);
            Assert.AreEqual("Ready", view.Windows.IntelligenceStatus);
            Assert.IsTrue(view.Windows.Stale); // Missing HostSystem data cannot establish the current OS.
            Assert.AreEqual(1, view.Windows.Items.Single().Cves.Single(x => x.Cve == "CVE-2025-1234").CandidateDocuments);
            Assert.AreEqual(0, view.Windows.Items.Single().Cves.Single(x => x.Cve == "CVE-2025-9999").CandidateDocuments);
            await PublishHost(new InventoryEntry("host", "machine", "10.0.20348", "X64", OperatingSystem: "Windows",
                ProcessArchitecture: "X64", Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Windows",
                WindowsEdition: "ServerStandard", WindowsRelease: "21H2", WindowsBuild: 20348, WindowsRevision: 1,
                WindowsInstallationType: "Server Core"), 1, InventoryChunkCodec.WindowsUpdateSchemaVersion);
            using var catalog = new MicrosoftCatalogCache((_, _, _) => throw new InvalidOperationException("No installed KB requires a lookup."), TimeProvider.System);
            var evaluation = await new WindowsProductEvaluation(factory, TimeProvider.System, catalog).EvaluateAsync(owner, TimeSpan.FromDays(14), token);
            Assert.AreEqual(WindowsEvaluationStatus.Ready, evaluation.Status);
            Assert.AreEqual("Windows Server 2022 (Server Core installation)", evaluation.Product);
            Assert.IsNotNull(evaluation.HostSnapshotId);
            Assert.IsTrue(evaluation.Candidates!.Any(x => x.Cve == "CVE-2025-1234"));
            Assert.AreEqual("Moderate", evaluation.Cves!.Single(x => x.Cve == "CVE-2025-1234").MicrosoftSeverity);
            Assert.IsNotNull(evaluation.Updates);
            Assert.AreEqual(InventoryOutcome.Partial, evaluation.Updates.Outcome);
            Assert.AreEqual(chunk.SnapshotId, evaluation.Updates.SnapshotId);
            Assert.IsNull(evaluation.QuickFix);
            Assert.IsEmpty(evaluation.Cves!.Single(x => x.Cve == "CVE-2025-1234").ObservedKbs);
            Assert.IsNotNull(evaluation.Evidence);
            Assert.IsEmpty(evaluation.Evidence.Items); // The assessment retains version evidence, not whole parsed documents.
            Assert.IsFalse(evaluation.CoverageComplete);
            Assert.IsTrue(evaluation.CatalogComplete);
            Assert.IsNotNull(evaluation.CatalogCheckedAt);
            await using (var fenceDb = factory.CreateDbContext())
            await using (var transaction = await fenceDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token))
            {
                Assert.IsFalse(await WindowsAssessmentFence.LockCurrentAsync(fenceDb, factory, new OwnerFence(false), owner, evaluation, TimeProvider.System, TimeSpan.FromDays(14), token));
                Assert.IsTrue(await WindowsAssessmentFence.LockCurrentAsync(fenceDb, factory, new OwnerFence(true), owner, evaluation, TimeProvider.System, TimeSpan.FromDays(14), token));
                Assert.IsFalse(await WindowsAssessmentFence.LockCurrentAsync(fenceDb, factory, new OwnerFence(true), owner, evaluation,
                    new FinalInventoryExpiryClock(DateTimeOffset.UtcNow), TimeSpan.FromHours(1), token));
                await transaction.RollbackAsync(token);
            }
            var writer = new WindowsAssessmentWriter(factory, new OwnerFence(true), TimeProvider.System);
            Assert.AreEqual(WindowsWriteResult.Written, await writer.WriteAsync(owner, evaluation, TimeSpan.FromDays(14), token));
            Assert.AreEqual(WindowsWriteResult.AlreadyWritten, await writer.WriteAsync(owner, evaluation, TimeSpan.FromDays(14), token));
            var conflicting = evaluation with { Cves = evaluation.Cves!.Select(x => x with { RebootObserved = !x.RebootObserved }).ToArray() };
            Assert.AreEqual(WindowsWriteResult.Conflict, await writer.WriteAsync(owner, conflicting, TimeSpan.FromDays(14), token));
            var failingOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security"))
                .AddInterceptors(new FailThirdSave()).Options;
            var failedRunId = Guid.CreateVersion7();
            var many = evaluation with { RunId = failedRunId, Cves = Enumerable.Range(0, 129)
                .Select(i => new WindowsCveEvidence($"CVE-2025-{i + 4000}", [], false, false, false)).ToArray() };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new WindowsAssessmentWriter(new Factory(failingOptions), new OwnerFence(true), TimeProvider.System)
                .WriteAsync(owner, many, TimeSpan.FromDays(14), token));
            Assert.IsFalse(await db.VulnerabilityRuns.AnyAsync(x => x.RunId == failedRunId, token));
            Assert.IsFalse(await db.VulnerabilityEvaluations.AnyAsync(x => x.RunId == failedRunId, token));
            Assert.IsFalse(await db.VulnerabilityInventoryEvidence.AnyAsync(x => x.RunId == failedRunId, token));
            Assert.AreEqual(3, await db.VulnerabilityInventoryEvidence.CountAsync(x => x.RunId == evaluation.RunId, token));
            Assert.IsFalse((await db.VulnerabilityRuns.AsNoTracking().SingleAsync(x => x.RunId == evaluation.RunId, token)).ProductDiscoveryComplete.GetValueOrDefault(true));
            Assert.IsTrue((await db.VulnerabilityRuns.AsNoTracking().SingleAsync(x => x.RunId == evaluation.RunId, token)).CatalogComplete);
            Assert.AreEqual(1, await db.VulnerabilityEvaluations.CountAsync(x => x.RunId == evaluation.RunId, token));
            var savedEvaluation = await db.VulnerabilityEvaluations.AsNoTracking().SingleAsync(x => x.RunId == evaluation.RunId, token);
            Assert.AreEqual((int)MatchState.Unknown, savedEvaluation.State);
            Assert.IsNotNull(savedEvaluation.WindowsEvidence);
            var current = await new WindowsAssessmentCurrent(factory, TimeProvider.System).GetAsync(owner, 1, null, null, null, token);
            Assert.IsNotNull(current);
            Assert.AreEqual(Vigiloom.Contracts.Vulnerabilities.VulnerabilityCoverageStatus.Current, current.Status);
            var expiresDuringRead = await new WindowsAssessmentCurrent(factory, new FinalReadExpiryClock(DateTimeOffset.UtcNow))
                .GetAsync(owner, 1, null, null, null, token);
            Assert.AreEqual(Vigiloom.Contracts.Vulnerabilities.VulnerabilityCoverageStatus.Stale, expiresDuringRead!.Status);
            Assert.IsFalse(current.WindowsProductDiscoveryComplete.GetValueOrDefault(true));
            Assert.IsTrue(current.WindowsCatalogComplete);
            var currentItem = current.Items.Single();
            Assert.AreEqual("CVE-2025-1234", currentItem.AdvisoryId);
            Assert.IsNotNull(currentItem.WindowsDetails);
            Assert.AreEqual("Moderate", currentItem.WindowsDetails.MicrosoftSeverity);
            // Ranking must apply before pagination and retain its source versions between pages.
            var epssArtifact = new byte[18];
            var epssSource = new IntelligenceSource { Key = EpssSnapshotStage.SourceKey, Enabled = true, LastSuccessfulCheckAt = now };
            var epssRevision = new IntelligenceRevision { SourceId = epssSource.Id, UpstreamVersion = "v1", RetrievedAt = now, ReleasedAt = now,
                Digest = System.Security.Cryptography.SHA256.HashData(epssArtifact), Artifact = epssArtifact, SchemaVersion = EpssSnapshotStage.Schema,
                MappingVersion = EpssSnapshotStage.Mapping, LicenseEvidence = EpssRefresh.LicenseEvidence, RecordCount = 1, Normalized = true };
            var kevSource = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = now };
            var kevRevision = new IntelligenceRevision { SourceId = kevSource.Id, UpstreamVersion = "v1", RetrievedAt = now,
                Digest = System.Security.Cryptography.SHA256.HashData([1]), Artifact = [1], SchemaVersion = KevCatalogValidator.SchemaRevision,
                MappingVersion = KevUpdate.MappingVersion, LicenseEvidence = KevUpdate.LicenseEvidence, RecordCount = 1, Normalized = true };
            db.IntelligenceSources.AddRange(epssSource, kevSource);
            db.IntelligenceRevisions.AddRange(epssRevision, kevRevision);
            db.IntelligenceActivations.AddRange(new IntelligenceActivation { SourceId = epssSource.Id, RevisionId = epssRevision.Id, ActivatedAt = now },
                new IntelligenceActivation { SourceId = kevSource.Id, RevisionId = kevRevision.Id, ActivatedAt = now });
            db.IntelligenceActivationEvents.Add(new IntelligenceActivationEvent { SourceId = kevSource.Id, RevisionId = kevRevision.Id, Scheduled = true, OccurredAt = now });
            var added = new List<Guid>();
            for (var i = 0; i < 52; i++)
            {
                var finding = new Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities.VulnerabilityFinding
                {
                    DeviceId = owner.DeviceId, SourceId = source.Id, IdentityHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"windows-rank-{i}")),
                    PackageKey = savedEvaluation.Ecosystem, AdvisoryId = $"CVE-2025-{i + 4000}", CurrentState = savedEvaluation.State,
                    FirstEvaluatedAt = now, LastEvaluatedAt = now, LastRevisionId = savedEvaluation.RevisionId, LastRunId = savedEvaluation.RunId
                };
                added.Add(finding.Id);
                db.VulnerabilityFindings.Add(finding);
                db.VulnerabilityEvaluations.Add(new Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities.VulnerabilityEvaluation
                {
                    FindingId = finding.Id, RunId = savedEvaluation.RunId, SourceId = source.Id, RevisionId = savedEvaluation.RevisionId,
                    AgentId = owner.AgentId, DeviceGeneration = owner.DeviceGeneration, HostSnapshotId = savedEvaluation.HostSnapshotId,
                    EvaluatedAt = now, MatcherVersion = savedEvaluation.MatcherVersion, Ecosystem = savedEvaluation.Ecosystem,
                    Architecture = savedEvaluation.Architecture, State = savedEvaluation.State, WindowsEvidence = savedEvaluation.WindowsEvidence
                });
            }
            db.KevRecords.Add(new KevRecord { RevisionId = kevRevision.Id, CveId = "CVE-2025-4050", VendorProject = "test", Product = "test", VulnerabilityName = "test", Description = "test", RequiredAction = "test" });
            db.EpssScores.Add(new EpssScoreRecord { RevisionId = epssRevision.Id, Cve = "CVE-2025-4051", Probability = 0.99m, Percentile = 0.99m });
            await db.SaveChangesAsync(token);
            var riskKev = await new KevCatalog(factory, TimeProvider.System).GetActiveAsync(token);
            var rankedWindows = await new WindowsAssessmentCurrent(factory, TimeProvider.System).GetAsync(owner, 1, null, null, null, token, riskKev);
            Assert.IsNotNull(rankedWindows);
            Assert.AreEqual("CVE-2025-4050", rankedWindows.Items[0].AdvisoryId);
            Assert.IsTrue(rankedWindows.Items[0].KnownExploited);
            Assert.AreEqual("CVE-2025-4051", rankedWindows.Items[1].AdvisoryId);
            Assert.AreEqual(0.99m, rankedWindows.Items[1].EpssProbability);
            Assert.IsTrue(rankedWindows.HasMore);
            var rankedSecond = await new WindowsAssessmentCurrent(factory, TimeProvider.System).GetAsync(owner, 2, null, null, rankedWindows.RankingVersion, token, riskKev);
            Assert.IsNotNull(rankedSecond);
            Assert.HasCount(3, rankedSecond.Items);
            Assert.IsFalse(rankedWindows.Items.Select(x => x.Id).Intersect(rankedSecond.Items.Select(x => x.Id)).Any());
            await db.IntelligenceSources.Where(x => x.Id == epssSource.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Enabled, false), token);
            var rankingChanged = await new WindowsAssessmentCurrent(factory, TimeProvider.System).GetAsync(owner, 2, null, null, rankedWindows.RankingVersion, token, riskKev);
            Assert.IsTrue(rankingChanged!.RankingChanged);
            var noRisk = await new WindowsAssessmentCurrent(factory, TimeProvider.System).GetAsync(owner, 1, null, null, null, token, includeRisk: false);
            Assert.AreEqual("Unavailable", noRisk!.EpssStatus);
            Assert.IsTrue(noRisk.Items.All(x => x.KnownExploited is null && x.EpssProbability is null));
            await db.VulnerabilityEvaluations.Where(x => added.Contains(x.FindingId)).ExecuteDeleteAsync(token);
            await db.VulnerabilityFindings.Where(x => added.Contains(x.Id)).ExecuteDeleteAsync(token);
            var history = await new VulnerabilityHistory(factory).GetAsync(owner.DeviceId, currentItem.Id, now.AddDays(-1), now.AddDays(1), null, token);
            Assert.IsNotNull(history);
            Assert.IsNotNull(history.Items.Single().WindowsDetails);
            Assert.AreEqual("Moderate", history.Items.Single().WindowsDetails!.MicrosoftSeverity);
            Assert.HasCount(3, history.Items.Single().WindowsSnapshots!);
            Assert.IsTrue(history.Items.Single().WindowsSnapshots!.Any(x => x.Source == InventorySource.WindowsUpdate && x.Outcome == InventoryOutcome.Partial));
            var reassessment = new WindowsReassessment(factory, new OwnerResolver(owner), new OwnerFence(true), TimeProvider.System, NullLogger<WindowsReassessment>.Instance);
            await reassessment.RunAsync(token);
            Assert.AreEqual(1, await db.VulnerabilityRuns.CountAsync(x => x.SourceId == source.Id, token));
            view = await new WindowsVulnerabilityEvidence(factory, TimeProvider.System, NullLogger.Instance).GetAsync(owner, 1, token);
            Assert.IsNotNull(view?.Windows);
            Assert.IsFalse(view.Windows.Stale);
            Assert.AreEqual("Windows Server 2022 (Server Core installation)", view.Windows.ProductName);
            Assert.AreEqual(20348, view.Windows.Build);
            Assert.AreEqual(1, view.Windows.BuildRevision);
            Assert.IsFalse(view.Windows.Items.Single().Installed);
            Assert.HasCount(20, view.Windows.Items.Single().KnowledgeBaseIds!);
            Assert.AreEqual("1234567", view.Windows.Items.Single().KnowledgeBaseIds![0]);
            Assert.IsTrue(view.Windows.Items.Single().KnowledgeBaseIdsTruncated);
            Assert.AreEqual(0, view.Windows.Items.Single().ServerSelection);
            Assert.AreEqual(Guid.Empty, view.Windows.Items.Single().ServiceId);
            CollectionAssert.AreEqual(new[] { "22222222-2222-4222-8222-222222222222" }, view.Windows.Items.Single().SupersededUpdateIds!.ToArray());
            Assert.AreEqual(1, view.Windows.Items.Single().Cves.Single(x => x.Cve == "CVE-2025-1234").ProductDocuments);
            Assert.IsTrue(view.Windows.Items.Single().Cves.Single(x => x.Cve == "CVE-2025-1234").HasCustomerActionNotice);
            var remediation = view.Windows.Items.Single().Cves.Single(x => x.Cve == "CVE-2025-1234").Remediations!.Single();
            Assert.AreEqual("2025-Nov", remediation.DocumentId);
            Assert.AreEqual("1234567", remediation.Description);
            Assert.AreEqual("10.0.20348.3000", remediation.FixedBuild);
            Assert.IsTrue(remediation.MatchesObservedKb);
            var interceptor = new FinalSourceGate();
            var gatedOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security"))
                .AddInterceptors(interceptor).Options;
            using var pendingCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = new WindowsVulnerabilityEvidence(new Factory(gatedOptions), TimeProvider.System, NullLogger.Instance).GetAsync(owner, 1, pendingCancel.Token);
            try
            {
                await interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                await PublishHost(new InventoryEntry("host", "machine", "10.0.20348", "X64", OperatingSystem: "Windows",
                    ProcessArchitecture: "X64", Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Windows",
                    WindowsEdition: "ServerStandard", WindowsRelease: "21H2", WindowsBuild: 20348, WindowsRevision: 2,
                    WindowsInstallationType: "Server Core"), 2, InventoryChunkCodec.WindowsUpdateSchemaVersion);
            }
            catch (Exception error)
            {
                var errors = new List<Exception> { error };
                try { pendingCancel.Cancel(); }
                catch (Exception cancellationError) { errors.Add(cancellationError); }
                interceptor.Release.TrySetResult();
                try { await pending; }
                catch (OperationCanceledException) when (pendingCancel.IsCancellationRequested) { }
                catch (Exception pendingError) { errors.Add(pendingError); }
                if (errors.Count > 1) throw new AggregateException(errors);
                throw;
            }
            finally { interceptor.Release.TrySetResult(); }
            var staleAssessment = await new WindowsAssessmentCurrent(factory, TimeProvider.System).GetAsync(owner, 1, null, null, null, token);
            Assert.AreEqual(Vigiloom.Contracts.Vulnerabilities.VulnerabilityCoverageStatus.Stale, staleAssessment!.Status);
            await using (var fenceDb = factory.CreateDbContext())
            await using (var transaction = await fenceDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token))
            {
                Assert.IsFalse(await WindowsAssessmentFence.LockCurrentAsync(fenceDb, factory, new OwnerFence(true), owner, evaluation, TimeProvider.System, TimeSpan.FromDays(14), token));
                await transaction.RollbackAsync(token);
            }
            var changedHost = await pending;
            await reassessment.RunAsync(token);
            Assert.AreEqual(2, await db.VulnerabilityRuns.CountAsync(x => x.SourceId == source.Id, token));
            Assert.IsNotNull(changedHost?.Windows);
            Assert.IsTrue(changedHost.Windows.Stale);
            Assert.IsNull(changedHost.Windows.Build);
            Assert.IsTrue(changedHost.Windows.Items.SelectMany(x => x.Cves).All(x => x.ProductDocuments is null && x.Remediations is null));
            var expiresAfterLookup = await new WindowsVulnerabilityEvidence(factory, new FinalCheckExpiryClock(DateTimeOffset.UtcNow), NullLogger.Instance).GetAsync(owner, 1, token);
            Assert.IsNotNull(expiresAfterLookup?.Windows);
            Assert.IsFalse(expiresAfterLookup.Windows.Stale);
            Assert.AreEqual("Changed", expiresAfterLookup.Windows.IntelligenceStatus);
            Assert.IsTrue(expiresAfterLookup.Windows.Items.SelectMany(x => x.Cves).All(x => x.CandidateDocuments is null && x.ProductDocuments is null && x.Remediations is null && !x.HasCustomerActionNotice));
            var staleView = await new WindowsVulnerabilityEvidence(factory, new FixedClock(now.AddDays(15)), NullLogger.Instance).GetAsync(owner, 1, token);
            Assert.IsNotNull(staleView?.Windows);
            Assert.IsTrue(staleView.Windows.Stale);
            Assert.IsNull(staleView.Windows.Build);
            Assert.IsTrue(staleView.Windows.Items.SelectMany(x => x.Cves).All(x => x.Remediations is null && x.ProductDocuments is null));
            Assert.AreEqual(0, view.Windows.Items.Single().Cves.Single(x => x.Cve == "CVE-2025-9999").ProductDocuments);
            var patchEntry = new InventoryEntry("5031364", "installed patch");
            var patchMeasure = InventoryDigest.MeasureChunk([patchEntry]);
            var patchChunk = new InventoryChunk(InventoryChunkCodec.SchemaVersion, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.WindowsQuickFix, now, InventoryOutcome.Complete, 0, 1, 1, patchMeasure.Bytes, InventoryDigest.Combine([patchMeasure.Digest]), [patchEntry]);
            await using (var patchTransaction = await db.Database.BeginTransactionAsync(token))
            {
                var stagedPatch = await InventoryStaging.StoreAsync(db, owner, patchChunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, stagedPatch.Disposition);
                await InventoryPublication.FinishAsync(db, stagedPatch.State!, stagedPatch.Snapshot!, now, token);
                await patchTransaction.CommitAsync(token);
            }
            db.ChangeTracker.Clear();
            using var catalogWithRelation = new MicrosoftCatalogCache((_, _, _) => Task.FromResult(new CatalogRelationResult(CatalogRelationStatus.Ready,
                "db2ed84f-9b54-41f7-b056-eb5d42725946", ["1234567"])), TimeProvider.System);
            var withRelation = await new WindowsProductEvaluation(factory, TimeProvider.System, catalogWithRelation).EvaluateAsync(owner, TimeSpan.FromDays(14), token);
            Assert.AreEqual(WindowsEvaluationStatus.Ready, withRelation.Status);
            Assert.AreEqual("5031364", withRelation.Cves!.Single().CatalogReferences!.Single().InstalledKb);
            Assert.AreEqual(WindowsWriteResult.Written, await writer.WriteAsync(owner, withRelation, TimeSpan.FromDays(14), token));
            var savedWithRelation = await new WindowsAssessmentCurrent(factory, TimeProvider.System).GetAsync(owner, 1, null, null, null, token);
            Assert.AreEqual("1234567", savedWithRelation!.Items.Single().WindowsDetails!.CatalogReferences!.Single().SupersededKb);
            Assert.AreEqual((int)MatchState.Fixed, savedWithRelation.Items.Single().State);
            Assert.IsTrue(savedWithRelation.Items.Single().WindowsDetails!.HasCustomerActionNotice);
            var updatedHistory = await new VulnerabilityHistory(factory).GetAsync(owner.DeviceId, savedWithRelation.Items.Single().Id,
                now.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), null, token);
            Assert.IsNotNull(updatedHistory);
            Assert.IsTrue(updatedHistory.Items.Any(x => x.State == (int)MatchState.Fixed));
            Assert.IsTrue(updatedHistory.Items.Any(x => x.State == (int)MatchState.Unknown));
            await PublishHost(new InventoryEntry("host", "machine", "6.8", "X64", OperatingSystem: "Linux",
                ProcessArchitecture: "X64", Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Linux", DistributionId: "ubuntu", DistributionVersion: "24.04"), 3, InventoryChunkCodec.SchemaVersion);
            Assert.IsNull(await new WindowsVulnerabilityEvidence(factory, TimeProvider.System, NullLogger.Instance).GetAsync(owner, 1, token));
            source = await db.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token);
            var expiring = new MsrcCandidateLookup(factory, new ExpiringClock(source.LastSuccessfulCheckAt!.Value));
            Assert.AreEqual(MsrcCandidateStatus.Changed, (await expiring.FindAsync(source.Id, first.RevisionId.Value, ["CVE-2025-1234"], token)).Status);
            var original = (await db.MsrcArtifacts.AsNoTracking().SingleAsync(token)).Content;
            await db.MsrcArtifacts.ExecuteUpdateAsync(x => x.SetProperty(a => a.Content, new byte[] { 1 }), token);
            Assert.AreEqual(MsrcCandidateStatus.Unavailable, (await lookup.FindAsync(source.Id, first.RevisionId.Value, ["CVE-2025-1234"], token)).Status);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token));
            await db.MsrcArtifacts.ExecuteUpdateAsync(x => x.SetProperty(a => a.Content, original), token);
            var active = await db.IntelligenceActivations.AsNoTracking().SingleAsync(x => x.SourceId == source.Id, token);
            handler.ExtraCves = 0;
            var reduced = await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Staged, reduced.Status);
            Assert.AreEqual(1, await MsrcRevisionEligibility.AssociationCountAsync(db, source.Id, reduced.RevisionId!.Value, token));
            Assert.AreEqual(IntelligencePublicationResult.ConfirmationRequired,
                await publication.ActivateScheduledAsync(source.Id, reduced.RevisionId.Value, first.RevisionId, Convert.ToBase64String(active.Version), token));
            await PublishHost(new InventoryEntry("host", "machine", "10.0.20348", "X64", OperatingSystem: "Windows",
                ProcessArchitecture: "X64", Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Windows",
                WindowsEdition: "ServerStandard", WindowsBuild: 20348, WindowsRevision: 1), 4, InventoryChunkCodec.WindowsReleaseSchemaVersion);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE [security].[MsrcCveIndexes]", token);
            var withoutIntelligence = await new WindowsVulnerabilityEvidence(factory, TimeProvider.System, NullLogger.Instance).GetAsync(owner, 1, token);
            Assert.IsNotNull(withoutIntelligence?.Windows);
            Assert.AreEqual("Unavailable", withoutIntelligence.Windows.IntelligenceStatus);
            Assert.HasCount(1, withoutIntelligence.Windows.Items);
            Assert.IsTrue(withoutIntelligence.Windows.Items.Single().Cves.All(x => x.CandidateDocuments is null));

            async Task PublishHost(InventoryEntry entry, long generation, int schema)
            {
                var measure = InventoryDigest.MeasureChunk([entry]);
                var hostChunk = new InventoryChunk(schema, Guid.CreateVersion7(), now, Guid.CreateVersion7(), generation,
                    InventorySource.HostSystem, now, InventoryOutcome.Complete, 0, 1, 1, measure.Bytes, InventoryDigest.Combine([measure.Digest]), [entry]);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var stagedHost = await InventoryStaging.StoreAsync(db, owner, hostChunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, stagedHost.Disposition);
                await InventoryPublication.FinishAsync(db, stagedHost.State!, stagedHost.Snapshot!, now, token);
                await tx.CommitAsync(token);
                db.ChangeTracker.Clear();
            }
        }, context.CancellationToken);
    [TestMethod]
    public Task Refresh_IsDisabledByDefaultAndRetainsOnlyCompleteDownloads() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            using var handler = new MsrcHandler();
            var refresh = new MsrcRefresh(new Factory(options), new HttpFactory(handler), TimeProvider.System, new IntelligencePublication(new Factory(options), new OperationAuditWriter(), TimeProvider.System));
            Assert.AreEqual(MsrcRefreshResult.Disabled, await refresh.RunDueAsync(token));
            var source = await db.IntelligenceSources.SingleAsync(token);
            Assert.AreEqual(512 * 1024 * 1024, source.MaximumBytes);
            source.Enabled = true; await db.SaveChangesAsync(token); db.ChangeTracker.Clear();
            Assert.AreEqual(MsrcRefreshResult.Staged, await refresh.RunDueAsync(token));
            source = await db.IntelligenceSources.SingleAsync(token);
            Assert.IsNotNull(source.LastSuccessfulCheckAt);
            Assert.AreEqual((await db.IntelligenceRevisions.SingleAsync(token)).Id, source.LastObservedRevisionId);
            Assert.AreEqual(source.LastObservedRevisionId, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            Assert.IsTrue((await db.IntelligenceRevisions.SingleAsync(token)).Normalized);
            Assert.AreEqual(MsrcSnapshotStage.Mapping, (await db.IntelligenceRevisions.SingleAsync(token)).MappingVersion);
            var artifact = await db.MsrcArtifacts.AsNoTracking().SingleAsync(token);
            var products = await db.MsrcProductIndexes.AsNoTracking().ToArrayAsync(token);
            var document = MsrcCvrfReader.Read(artifact.Content, 64 * 1024 * 1024, 100_000);
            var expectedProducts = MsrcProductDiscovery.Discover(document);
            Assert.AreEqual(expectedProducts.Candidates.Count, artifact.IndexedProductCount);
            Assert.AreEqual(expectedProducts.Complete, artifact.ProductIndexComplete);
            Assert.IsTrue(products.Select(x => new MsrcProductCandidate(x.ProductName, x.Cve)).ToHashSet().SetEquals(expectedProducts.Candidates));
            var observed = source.LastObservedRevisionId;
            source.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-2); await db.SaveChangesAsync(token); db.ChangeTracker.Clear();
            handler.AddSecond = true;
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [security].[MsrcTestFailure] ON [security].[MsrcCveIndexes] AFTER INSERT AS THROW 51000, 'test insert failure', 1;", token);
            Assert.AreEqual(MsrcRefreshResult.Failed, await refresh.RunDueAsync(token));
            db.ChangeTracker.Clear();
            source = await db.IntelligenceSources.SingleAsync(token);
            Assert.IsNotNull(source.LastError);
            Assert.AreEqual(observed, source.LastObservedRevisionId);
            Assert.AreEqual(observed, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            Assert.AreEqual(1, await db.MsrcArtifacts.CountAsync(token));
            Assert.AreEqual(1, await db.MsrcCveIndexes.CountAsync(token));
            Assert.AreEqual(1, await db.MsrcDocuments.CountAsync(token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [security].[MsrcTestFailure]", token);
            source.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-2); await db.SaveChangesAsync(token); db.ChangeTracker.Clear();
            handler.AddSecond = false;
            handler.StaleDocument = true;
            Assert.AreEqual(MsrcRefreshResult.Failed, await refresh.RunDueAsync(token));
            source = await db.IntelligenceSources.SingleAsync(token);
            Assert.IsNotNull(source.LastError);
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_AutomaticallyPublishesNewVersionsAndPreservesRollbackUntilNewVersion() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            using var handler = new MsrcHandler();
            var factory = new Factory(options);
            var publication = new IntelligencePublication(factory, new NoopAudit(), TimeProvider.System);
            var refresh = new MsrcRefresh(factory, new HttpFactory(handler), TimeProvider.System, publication);
            Assert.AreEqual(MsrcRefreshResult.Disabled, await refresh.RunDueAsync(token));
            var source = await db.IntelligenceSources.SingleAsync(token);
            source.Enabled = true;
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            Assert.AreEqual(MsrcRefreshResult.Staged, await refresh.RunDueAsync(token));
            var first = (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId;
            await db.IntelligenceActivations.ExecuteDeleteAsync(token);
            await MakeDue();
            Assert.AreEqual(MsrcRefreshResult.Unchanged, await refresh.RunDueAsync(token));
            Assert.AreEqual(first, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            handler.AddSecond = true;
            await MakeDue();
            Assert.AreEqual(MsrcRefreshResult.Staged, await refresh.RunDueAsync(token));
            var second = await db.IntelligenceActivations.AsNoTracking().SingleAsync(token);
            Assert.AreNotEqual(first, second.RevisionId);
            var beforeConcurrentDownload = await VendorRefreshStatus.CaptureAsync(factory,
                await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token), token);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, first, second.RevisionId,
                Convert.ToBase64String(second.Version), true, Guid.CreateVersion7(), token));
            var concurrentObserved = Guid.CreateVersion7();
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.LastObservedRevisionId, concurrentObserved), token);
            await VendorRefreshStatus.MarkPublicationFailureAsync(factory, source.Id, concurrentObserved, beforeConcurrentDownload, token);
            Assert.IsNull((await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token)).LastError);
            await VendorRefreshStatus.PublishAsync(factory, publication, source.Id, concurrentObserved, beforeConcurrentDownload, token);
            Assert.AreEqual(first, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.LastObservedRevisionId, second.RevisionId), token);
            await MakeDue();
            Assert.AreEqual(MsrcRefreshResult.Unchanged, await refresh.RunDueAsync(token));
            Assert.AreEqual(first, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            handler.ExtraCves = 1;
            await MakeDue();
            Assert.AreEqual(MsrcRefreshResult.Staged, await refresh.RunDueAsync(token));
            var third = (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId;
            Assert.AreNotEqual(first, third);
            Assert.AreNotEqual(second.RevisionId, third);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [security].[PublicationTestFailure] ON [security].[IntelligenceActivationEvents] AFTER INSERT AS THROW 51000, 'test publication failure', 1;", token);
            handler.ExtraCves = 2;
            await MakeDue();
            await Assert.ThrowsAsync<DbUpdateException>(() => refresh.RunDueAsync(token));
            db.ChangeTracker.Clear();
            Assert.AreEqual(third, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            var afterFailure = await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token);
            Assert.AreEqual(VendorRefreshStatus.PublicationError, afterFailure.LastError);
            Assert.AreNotEqual(third, afterFailure.LastObservedRevisionId);
            handler.StaleDocument = true;
            await MakeDue();
            Assert.AreEqual(MsrcRefreshResult.Failed, await refresh.RunDueAsync(token));
            var afterDownloadFailure = await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token);
            Assert.IsTrue(VendorRefreshStatus.HasPublicationError(afterDownloadFailure.LastError));
            Assert.Contains("retrieval or validation failed", afterDownloadFailure.LastError!);
            handler.StaleDocument = false;
            Assert.IsTrue(await VendorRefreshStatus.UpdateAsync(factory, TimeProvider.System, afterDownloadFailure, null, afterDownloadFailure.LastObservedRevisionId, token));
            var beforeCancelSource = await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token);
            var beforeCancel = await VendorRefreshStatus.CaptureAsync(factory, beforeCancelSource, token);
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => VendorRefreshStatus.PublishAsync(factory, publication, source.Id,
                    beforeCancelSource.LastObservedRevisionId!.Value, beforeCancel, cancelled.Token));
            }
            Assert.IsTrue(VendorRefreshStatus.HasPublicationError((await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token)).LastError));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [security].[PublicationTestFailure]", token);
            await MakeDue();
            Assert.AreEqual(MsrcRefreshResult.Unchanged, await refresh.RunDueAsync(token));
            Assert.AreEqual(afterFailure.LastObservedRevisionId, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [security].[PublicationTestFailure] ON [security].[IntelligenceActivationEvents] AFTER INSERT AS THROW 51000, 'test publication failure', 1;", token);
            handler.ExtraCves = 3;
            await MakeDue();
            await Assert.ThrowsAsync<DbUpdateException>(() => refresh.RunDueAsync(token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [security].[PublicationTestFailure]", token);
            var activeBeforeRollback = await db.IntelligenceActivations.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, third, activeBeforeRollback.RevisionId,
                Convert.ToBase64String(activeBeforeRollback.Version), true, Guid.CreateVersion7(), token));
            Assert.IsNull((await db.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token)).LastError);
            await MakeDue();
            Assert.AreEqual(MsrcRefreshResult.Unchanged, await refresh.RunDueAsync(token));
            Assert.AreEqual(third, (await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).RevisionId);

            async Task MakeDue()
            {
                await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.LastCheckedAt, DateTimeOffset.UtcNow.AddDays(-2)), token);
                db.ChangeTracker.Clear();
            }
        }, context.CancellationToken);

    private sealed class NoopAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [TestMethod]
    public Task SourceLimits_AllowMeasuredCatalogAndProtectDowngrade() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, index);
            var migrator = db.GetService<IMigrator>();
            var source = new IntelligenceSource { Key = MsrcSnapshotStage.SourceKey, MaximumBytes = 512 * 1024 * 1024 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1], token));
            await db.IntelligenceSources.Where(x => x.Id == source.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.MaximumBytes, 64 * 1024 * 1024), token);
            await migrator.MigrateAsync(migrations[index - 1], token);
            await migrator.MigrateAsync(null, token);
        }, context.CancellationToken);
    [TestMethod]
    public Task Stage_StoresConsistentSnapshotAndReusesIdenticalContent() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = MsrcSnapshotStage.SourceKey, Enabled = true };
            db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token);
            using var handler = new MsrcHandler();
            using var http = new HttpClient(handler);
            var stage = new MsrcSnapshotStage(new Factory(options), TimeProvider.System);
            handler.ChangeCatalog = true;
            var conflict = await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Conflict, conflict.Status);
            Assert.AreEqual(1, await db.MsrcDocuments.CountAsync(token));
            handler.ChangeCatalog = false;
            handler.CatalogCalls = 0;
            handler.StaleDocument = true;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token));
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(token));
            handler.StaleDocument = false;
            var first = await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Staged, first.Status);
            Assert.AreEqual(1, await db.MsrcDocuments.CountAsync(token));
            Assert.IsTrue((await db.IntelligenceRevisions.AsNoTracking().SingleAsync(token)).Normalized);
            Assert.AreEqual(1, await db.MsrcCveIndexes.CountAsync(x => x.Cve == "CVE-2025-1234", token));
            Assert.AreEqual(1, (await db.MsrcArtifacts.AsNoTracking().SingleAsync(token)).IndexedCveCount);
            Assert.IsTrue(await MsrcRevisionEligibility.IsPublishableAsync(db, source.Id, first.RevisionId!.Value, token));
            await db.MsrcCveIndexes.ExecuteDeleteAsync(token);
            await db.MsrcArtifacts.ExecuteUpdateAsync(x => x.SetProperty(a => a.IndexedCveCount, -1), token);
            var productCount = await db.MsrcProductIndexes.CountAsync(token);
            Assert.IsGreaterThan(0, productCount);
            await db.MsrcProductIndexes.ExecuteDeleteAsync(token);
            await db.MsrcArtifacts.ExecuteUpdateAsync(x => x.SetProperty(a => a.IndexedProductCount, -1), token);
            Assert.IsFalse(await MsrcRevisionEligibility.IsPublishableAsync(db, source.Id, first.RevisionId!.Value, token));
            var again = await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Unchanged, again.Status);
            Assert.AreEqual(first.RevisionId, again.RevisionId);
            Assert.AreEqual(1, await db.MsrcCveIndexes.CountAsync(x => x.Cve == "CVE-2025-1234", token));
            Assert.AreEqual(1, (await db.MsrcArtifacts.AsNoTracking().SingleAsync(token)).IndexedCveCount);
            Assert.AreEqual(productCount, await db.MsrcProductIndexes.CountAsync(token));
            Assert.AreEqual(productCount, (await db.MsrcArtifacts.AsNoTracking().SingleAsync(token)).IndexedProductCount);
            Assert.IsTrue(await MsrcRevisionEligibility.IsPublishableAsync(db, source.Id, first.RevisionId!.Value, token));
            Assert.AreEqual(1, await db.MsrcDocuments.CountAsync(token));
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(token));
            await db.MsrcProductIndexes.ExecuteDeleteAsync(token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token));
            await db.MsrcArtifacts.ExecuteUpdateAsync(x => x.SetProperty(a => a.IndexedProductCount, -1), token);
            Assert.AreEqual(MsrcStageStatus.Unchanged, (await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token)).Status);
            Assert.AreEqual(productCount, await db.MsrcProductIndexes.CountAsync(token));
            handler.AddSecond = true;
            var expanded = await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Staged, expanded.Status);
            Assert.AreEqual(3, await db.MsrcDocuments.CountAsync(token));
            Assert.AreEqual(2, await db.MsrcArtifacts.CountAsync(token));
            Assert.AreEqual(2, await db.MsrcCveIndexes.CountAsync(x => x.Cve == "CVE-2025-1234", token));
            handler.AddSecond = false;
            await db.IntelligenceRevisions.Where(x => x.Id == first.RevisionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Revoked, true), token);
            var revoked = await stage.StageAsync(source.Id, source.Version, new MsrcDocumentClient(http), MsrcRefresh.LicenseEvidence, token);
            Assert.AreEqual(MsrcStageStatus.Unavailable, revoked.Status);
            Assert.AreEqual(2, await db.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class OwnerFence(bool current) : IAgentOwnerFence
    {
        public Task<bool> LockCurrentAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner, CancellationToken token) => Task.FromResult(current);
    }

    private sealed class FailThirdSave : SaveChangesInterceptor
    {
        private int saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref saves) == 3) throw new InvalidOperationException("Injected failure after the first finding batch.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class OwnerResolver(InventoryOwner owner) : IAgentOwnerResolver
    {
        public Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token) => Task.FromResult(deviceId == owner.DeviceId ? owner : null);
    }

    private sealed class ExpiringClock(DateTimeOffset start) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref calls) >= 3 ? start.AddYears(2) : start;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FinalInventoryExpiryClock(DateTimeOffset now) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref calls) >= 4 ? now.AddHours(2) : now;
    }

    private sealed class FinalReadExpiryClock(DateTimeOffset now) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref calls) >= 4 ? now.AddDays(15) : now;
    }

    private sealed class FinalSourceGate : DbCommandInterceptor
    {
        private int held;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal) && Interlocked.Exchange(ref held, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class FinalCheckExpiryClock(DateTimeOffset now) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref calls) >= 6 ? now.AddDays(4) : now;
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MsrcHandler : HttpMessageHandler
    {
        public bool StaleDocument { get; set; }
        public bool ChangeCatalog { get; set; }
        public bool AddSecond { get; set; }
        public int ExtraCves { get; set; }
        public bool ScopedExtraCves { get; set; }
        public int CatalogCalls { get; set; }
        public bool ActionRequired { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isCatalog = request.RequestUri!.AbsolutePath.EndsWith("/updates", StringComparison.Ordinal);
            if (isCatalog) CatalogCalls++;
            var second = AddSecond ? ",{\"ID\":\"2025-Dec\",\"CurrentReleaseDate\":\"2025-11-01T00:00:00Z\"}" : "";
            var content = isCatalog
                ? $"{{\"value\":[{{\"ID\":\"2025-Nov\",\"CurrentReleaseDate\":\"2025-11-0{(ChangeCatalog && CatalogCalls > 1 ? 2 : 1)}T00:00:00Z\"}}{second}]}}"
                : request.RequestUri.AbsolutePath.EndsWith("2025-Dec", StringComparison.Ordinal) ? MsrcCvrfReaderTests.Example.Replace("2025-Nov", "2025-Dec")
                : StaleDocument ? MsrcCvrfReaderTests.Example.Replace("2025-11-01T00:00:00", "2025-10-01T00:00:00") : MsrcCvrfReaderTests.Example;
            if (!isCatalog && ExtraCves > 0)
                content = content.Replace("\"Vulnerability\":[", "\"Vulnerability\":[" + string.Concat(Enumerable.Range(0, ExtraCves).Select(i => $"{{\"CVE\":\"CVE-2025-{i + 2000}\"{(ScopedExtraCves ? ",\"ProductStatuses\":[{\"Type\":3,\"ProductID\":[\"11924\"]}]" : "")}}},")));
            if (!isCatalog && ActionRequired)
                content = content.Replace("\"CVE\":\"CVE-2025-1234\"", "\"CVE\":\"CVE-2025-1234\",\"Notes\":[{\"Type\":6,\"Title\":\"Customer Action Required\",\"Value\":\"Yes\"}]");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) });
        }
    }
    [TestMethod]
    public Task Documents_EnforceRevisionIdentityAndPreserveEvidenceOnDowngrade() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, index);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[index - 1], token);
            await migrator.MigrateAsync(null, token);
            var source = new IntelligenceSource { Key = "msrc-cvrf" };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "test", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [1], SchemaVersion = "cvrf", MappingVersion = "test", LicenseEvidence = "test", RecordCount = 1 };
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            db.MsrcArtifacts.Add(new MsrcArtifact { Digest = new byte[32], Content = [1] });
            db.MsrcDocuments.Add(new MsrcStoredDocument { RevisionId = revision.Id, DocumentId = "2018-FEB", ModifiedAt = DateTimeOffset.UtcNow, Digest = new byte[32] });
            await db.SaveChangesAsync(token);
            db.MsrcDocuments.Add(new MsrcStoredDocument { RevisionId = revision.Id, DocumentId = "2018-FEB", ModifiedAt = DateTimeOffset.UtcNow, Digest = new byte[32] });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.ChangeTracker.Clear();
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1], token));
            Assert.AreEqual(1, await db.MsrcDocuments.CountAsync(token));
            await db.MsrcDocuments.ExecuteDeleteAsync(token);
            await db.MsrcArtifacts.ExecuteDeleteAsync(token);
            await migrator.MigrateAsync(migrations[index - 1], token);
        }, context.CancellationToken);
}
