using System.Net;
using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SignatureRefreshTests(TestContext context)
{
    private const string First = "eb8c53cb08ed103d031fe9c98d1d23e94904839d";

    [TestMethod]
    public Task Evidence_IsBoundedAndChecksEncodingAndIntegrity() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = SignatureRefresh.SchemaVersion, MappingVersion = SignatureRefresh.MappingVersion,
                LicenseEvidence = SignatureRefresh.LicenseEvidence, RecordCount = 1, Normalized = true };
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat("author " + new string('a', 600), 101)));
            var file = new SignatureFile { RevisionId = revision.Id, Path = "yara/example.yar", Kind = 1, Digest = SHA256.HashData(bytes), Content = bytes };
            await using (var db = contexts.CreateDbContext())
            { db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision); db.SignatureFiles.Add(file); await db.SaveChangesAsync(token); }
            var status = new IntelligenceStatus(contexts, TimeProvider.System);
            var evidence = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token);
            Assert.IsNotNull(evidence); Assert.IsTrue(evidence.Truncated); Assert.HasCount(100, evidence.Lines);
            Assert.IsTrue(evidence.Lines[0].Truncated); Assert.AreEqual(512, evidence.Lines[0].Text.Length);
            var secondPage = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token, page: 2);
            Assert.IsNotNull(secondPage); Assert.HasCount(1, secondPage.Lines); Assert.AreEqual(101, secondPage.Lines[0].Number); Assert.IsFalse(secondPage.Truncated);
            var context = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token, 51);
            Assert.IsNotNull(context?.Context); Assert.HasCount(21, context.Context);
            Assert.AreEqual(41, context.Context[0].Number); Assert.AreEqual(61, context.Context[^1].Number);
            Assert.IsTrue(context.ContextHasMore);
            var lastContext = await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token, 101);
            Assert.IsNotNull(lastContext?.Context); Assert.AreEqual(101, lastContext.Context[^1].Number); Assert.IsFalse(lastContext.ContextHasMore);
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.SignatureFiles.SingleAsync(token);
                saved.Content = [0xff]; saved.Digest = SHA256.HashData(saved.Content); await db.SaveChangesAsync(token);
            }
            Assert.IsTrue((await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token))!.InvalidEncoding);
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.SignatureFiles.SingleAsync(token);
                saved.Content = [1]; await db.SaveChangesAsync(token);
            }
            Assert.IsFalse((await status.GetSignatureEvidenceAsync(source.Id, revision.Id, file.Path, token))!.IntegrityValid);
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_StoresVersionAndPreservesItWhenNewLicenseIsInvalid() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var invalid = false;
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{(invalid ? new string('b', 40) : First)}\"}}") };
                using var archive = SignatureArchiveTests.Archive(changed: invalid, commit: invalid ? new string('b', 40) : First);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            var refresh = new SignatureRefresh(contexts, clients, clock);
            Assert.AreEqual(SignatureRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                var revision = await db.IntelligenceRevisions.SingleAsync(token);
                Assert.AreEqual(5, revision.RecordCount);
                Assert.AreEqual(5, await db.SignatureFiles.CountAsync(token));
                Assert.AreEqual(1, await db.IocEntries.CountAsync(token));
                Assert.AreEqual("invalid-hash", (await db.IocEntries.SingleAsync(token)).Reason);
                Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
                var source = await db.IntelligenceSources.SingleAsync(token);
                Assert.IsFalse(await IocSourceEligibility.IsEligibleAsync(db, source.Id, revision.Id, token));
                var files = await new IntelligenceStatus(contexts, clock).GetSignatureFilesAsync(source.Id, revision.Id, 1, token);
                Assert.IsNotNull(files);
                Assert.HasCount(5, files.Items);
                Assert.IsTrue(files.Items.Any(x => x.Kind == "ioc"));
                Assert.IsTrue(files.Items.Any(x => x.Kind == "yara"));
                var evidence = await new IntelligenceStatus(contexts, clock).GetSignatureEvidenceAsync(source.Id, revision.Id, "yara/example.yar", token);
                Assert.IsNotNull(evidence);
                Assert.IsTrue(evidence.IntegrityValid);
                Assert.HasCount(1, evidence.Lines);
                Assert.Contains("author", evidence.Lines[0].Text);
                Assert.IsNull(await new IntelligenceStatus(contexts, clock).GetSignatureEvidenceAsync(source.Id, revision.Id, "iocs/hash-iocs.txt", token));
                Assert.IsNull(await new IntelligenceStatus(contexts, clock).GetSignatureFilesAsync(Guid.CreateVersion7(), revision.Id, 1, token));
            }
            clock.Advance(TimeSpan.FromDays(2));
            invalid = true;
            Assert.AreEqual(SignatureRefreshResult.Failed, await refresh.RunDueAsync(token));
            await using var final = contexts.CreateDbContext();
            Assert.AreEqual(1, await final.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(5, await final.SignatureFiles.CountAsync(token));
            Assert.AreEqual(1, await final.IocEntries.CountAsync(token));
            Assert.AreEqual("Signature-Base license unavailable or changed.", (await final.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public Task ReviewedIocFiles_CanBeSelectedAndReadWithTransactionalAudit() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true, LastSuccessfulCheckAt = clock.GetUtcNow() };
            var artifact = "reviewed-source"u8.ToArray();
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = clock.GetUtcNow(),
                Digest = SHA256.HashData(artifact), Artifact = artifact, SchemaVersion = SignatureRefresh.SchemaVersion,
                MappingVersion = SignatureRefresh.MappingVersion, LicenseEvidence = SignatureRefresh.LicenseEvidence, RecordCount = 4, Normalized = true };
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                db.IntelligenceRevisions.Add(revision);
                foreach (var (path, fixture, kind, compressed) in new[] {
                    ("LICENSE", "LICENSE", 2, false), ("README.md", "README.md", 2, false),
                    (IocSourceEligibility.HashPath, "hash-iocs.txt.gz", 0, true), (IocSourceEligibility.C2Path, "c2-iocs.txt.gz", 0, true) })
                {
                    using var input = typeof(SignatureRefreshTests).Assembly.GetManifestResourceStream($"Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence.Fixtures.SignatureBase.{fixture}")!;
                    using var uncompressed = new MemoryStream();
                    if (compressed) { using var gzip = new GZipStream(input, CompressionMode.Decompress); await gzip.CopyToAsync(uncompressed, token); }
                    else await input.CopyToAsync(uncompressed, token);
                    var bytes = uncompressed.ToArray();
                    db.SignatureFiles.Add(new SignatureFile { RevisionId = revision.Id, Path = path, Kind = kind, Content = bytes, Digest = SHA256.HashData(bytes) });
                }
                await db.SaveChangesAsync(token);
            }
            var actor = Guid.CreateVersion7();
            var fail = new IntelligencePublication(contexts, new TestAudit(true), clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fail.ActivateAsync(source.Id, revision.Id, null, "", false, actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
                Assert.AreEqual(0, await db.IntelligenceActivationEvents.CountAsync(token));
            }
            var audit = new TestAudit(false);
            var publication = new IntelligencePublication(contexts, audit, clock);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(source.Id, revision.Id, null, "", false, actor, token));
            Assert.AreEqual(1, audit.Count);
            var consumer = new IocSource(contexts, clock);
            var selection = await consumer.GetActiveAsync(token);
            Assert.AreEqual(IocSourceAvailability.Ready, selection.Availability);
            var content = await consumer.ReadFileAsync(selection, IocSourceEligibility.HashPath, token);
            Assert.AreEqual(IocSourceAvailability.Ready, content.Availability);
            Assert.AreEqual("c11894bdde311ce0deebcdf29bfa1d6dcad9fc545af2a1fc7dbc748a6b1eabfa", Convert.ToHexStringLower(SHA256.HashData(content.Content!)));
            var c2 = await consumer.ReadFileAsync(selection, IocSourceEligibility.C2Path, token);
            Assert.AreEqual(IocSourceAvailability.Ready, c2.Availability);
            Assert.AreEqual("d7116ac4aa0d7521376a6fd56f05194eacedefac54eefcff7ca49a0db233cc86", Convert.ToHexStringLower(SHA256.HashData(c2.Content!)));
        }, context.CancellationToken);

    [TestMethod]
    public Task IocSource_AcceptsChangedDataAndRequiresConfirmationForReduction() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true, LastSuccessfulCheckAt = clock.GetUtcNow() };
            await using var db = contexts.CreateDbContext();
            db.IntelligenceSources.Add(source);
            var first = AddRevision("first", string.Join('\n', Enumerable.Range(0, 10).Select(x => $"host{x}.example.org")), new string('a', 32));
            var ninety = AddRevision("ninety", string.Join('\n', Enumerable.Range(0, 9).Select(x => $"host{x}.example.org")), new string('a', 32));
            var reduced = AddRevision("reduced", "# Copyright Example\n" + string.Join('\n', Enumerable.Repeat("new.example.org", 20)));
            var compensated = AddRevision("compensated", string.Join('\n', Enumerable.Range(0, 20).Select(x => $"new{x}.example.org")));
            var conflicting = AddRevision("conflicting", "# license: other terms\nnew.example.org");
            var restricted = AddRevision("restricted", "# For non-commercial use only.\nnew.example.org");
            var reserved = AddRevision("reserved", "# Copyright Example. All rights reserved.\nnew.example.org");
            var restrictedHash = AddRevision("restricted-hash", "new.example.org", new string('a', 32) + ";Non-commercial use only");
            var differentTerms = AddRevision("different-terms", "# GPL-3.0\nnew.example.org");
            var empty = AddRevision("empty", "# no indicators");
            await db.SaveChangesAsync(token);
            var publisher = new IntelligencePublication(contexts, new TestAudit(false), clock);
            var actor = Guid.CreateVersion7();
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(source.Id, first.Id, null, "", false, actor, token));
            var active = await db.IntelligenceActivations.AsNoTracking().SingleAsync(token);
            var version = Convert.ToBase64String(active.Version);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(source.Id, ninety.Id, first.Id, version, false, actor, token));
            version = Convert.ToBase64String((await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).Version);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(source.Id, first.Id, ninety.Id, version, false, actor, token));
            version = Convert.ToBase64String((await db.IntelligenceActivations.AsNoTracking().SingleAsync(token)).Version);
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publisher.ActivateAsync(source.Id, conflicting.Id, first.Id, version, true, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publisher.ActivateAsync(source.Id, restricted.Id, first.Id, version, true, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publisher.ActivateAsync(source.Id, reserved.Id, first.Id, version, true, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publisher.ActivateAsync(source.Id, restrictedHash.Id, first.Id, version, true, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publisher.ActivateAsync(source.Id, differentTerms.Id, first.Id, version, true, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publisher.ActivateAsync(source.Id, empty.Id, first.Id, version, true, actor, token));
            var priorFile = db.SignatureFiles.Local.Single(x => x.RevisionId == first.Id && x.Path == IocSourceEligibility.C2Path);
            var originalContent = priorFile.Content;
            priorFile.Content = [1];
            await db.SaveChangesAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.ConfirmationRequired, await publisher.ActivateAsync(source.Id, compensated.Id, first.Id, version, false, actor, token));
            priorFile.Content = originalContent;
            await db.SaveChangesAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.ConfirmationRequired, await publisher.ActivateAsync(source.Id, compensated.Id, first.Id, version, false, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.ConfirmationRequired, await publisher.ActivateAsync(source.Id, reduced.Id, first.Id, version, false, actor, token));
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(source.Id, reduced.Id, first.Id, version, true, actor, token));
            var catalog = new IocSource(contexts, clock);
            var selection = await catalog.GetActiveAsync(token);
            Assert.AreEqual(1, selection.ValidCount);
            Assert.AreEqual(IocSourceAvailability.SelectionChanged,
                (await catalog.ReadFileAsync(selection with { ActivationVersion = version }, IocSourceEligibility.C2Path, token)).Availability);
            Assert.AreEqual(IocSourceAvailability.Unavailable,
                (await catalog.ReadFileAsync(selection with { ManifestSha256 = new string('0', 64) }, IocSourceEligibility.C2Path, token)).Availability);

            IntelligenceRevision AddRevision(string label, string c2, string hashes = "# no hashes")
            {
                var artifact = Encoding.UTF8.GetBytes(label);
                var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = clock.GetUtcNow(),
                    Digest = SHA256.HashData(artifact), Artifact = artifact, SchemaVersion = SignatureRefresh.SchemaVersion,
                    MappingVersion = SignatureRefresh.MappingVersion, LicenseEvidence = SignatureRefresh.LicenseEvidence, RecordCount = 4, Normalized = true };
                db.IntelligenceRevisions.Add(revision);
                Add("LICENSE", Resource("LICENSE"), 2); Add("README.md", Resource("README.md"), 2);
                Add(IocSourceEligibility.HashPath, Encoding.UTF8.GetBytes(hashes), 0);
                Add(IocSourceEligibility.C2Path, Encoding.UTF8.GetBytes(c2), 0);
                return revision;
                void Add(string path, byte[] bytes, int kind) => db.SignatureFiles.Add(new SignatureFile
                    { RevisionId = revision.Id, Path = path, Kind = kind, Content = bytes, Digest = SHA256.HashData(bytes) });
                static byte[] Resource(string name)
                {
                    using var input = typeof(SignatureRefreshTests).Assembly.GetManifestResourceStream($"Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence.Fixtures.SignatureBase.{name}")!;
                    using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
                }
            }
        }, context.CancellationToken);

    [TestMethod]
    public Task IocSource_RequiresReviewedFilesAndRejectsNestedTermsOrChangedContent() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                using var archive = SignatureArchiveTests.Archive(includeC2: true);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            Assert.AreEqual(SignatureRefreshResult.Staged, await new SignatureRefresh(contexts, clients, clock).RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            var source = await db.IntelligenceSources.SingleAsync(token);
            var revision = await db.IntelligenceRevisions.SingleAsync(token);
            Assert.IsTrue(await IocSourceEligibility.IsEligibleAsync(db, source.Id, revision.Id, token));
            db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
            var catalog = new IocSource(contexts, clock);
            var selection = await catalog.GetActiveAsync(token);
            Assert.AreEqual(IocSourceAvailability.Ready, selection.Availability);
            var content = await catalog.ReadFileAsync(selection, IocSourceEligibility.HashPath, token);
            Assert.AreEqual(IocSourceAvailability.Ready, content.Availability);
            CollectionAssert.AreEqual("hash;description"u8.ToArray(), content.Content);
            Assert.AreEqual("DRL-1.1", content.License);
            Assert.AreEqual($"https://github.com/Neo23x0/signature-base/blob/{First}/LICENSE", content.LicenseUrl);
            Assert.AreEqual(IocSourceAvailability.SelectionChanged,
                (await catalog.ReadFileAsync(selection with { ActivationVersion = "invalid" }, IocSourceEligibility.HashPath, token)).Availability);
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.ReadFileAsync(selection, "yara/example.yar", token));
            var file = await db.SignatureFiles.SingleAsync(x => x.Path == IocSourceEligibility.HashPath, token);
            var original = file.Content;
            file.Content = [1];
            await db.SaveChangesAsync(token);
            Assert.IsFalse(await IocSourceEligibility.IsEligibleAsync(db, source.Id, revision.Id, token));
            Assert.AreEqual(IocSourceAvailability.Unavailable, (await catalog.ReadFileAsync(selection, IocSourceEligibility.HashPath, token)).Availability);
            file.Content = original;
            var exception = new SignatureFile { RevisionId = revision.Id, Path = "iocs/LICENSE.extra", Kind = 2,
                Content = [1], Digest = SHA256.HashData([1]) };
            db.SignatureFiles.Add(exception);
            revision.RecordCount++;
            await db.SaveChangesAsync(token);
            Assert.IsFalse(await IocSourceEligibility.IsEligibleAsync(db, source.Id, revision.Id, token));
            db.SignatureFiles.Remove(exception);
            revision.RecordCount--;
            await db.SaveChangesAsync(token);
            Assert.IsTrue(await IocSourceEligibility.IsEligibleAsync(db, source.Id, revision.Id, token));
            revision.Revoked = true;
            await db.SaveChangesAsync(token);
            Assert.AreEqual(IocSourceAvailability.Unavailable, (await catalog.ReadFileAsync(selection, IocSourceEligibility.HashPath, token)).Availability);
            revision.Revoked = false;
            source.Enabled = false;
            await db.SaveChangesAsync(token);
            Assert.AreEqual(IocSourceAvailability.Disabled, (await catalog.ReadFileAsync(selection, IocSourceEligibility.HashPath, token)).Availability);
            source.Enabled = true;
            await db.SaveChangesAsync(token);
            clock.Advance(TimeSpan.FromDays(31));
            var stale = await catalog.ReadFileAsync(selection, IocSourceEligibility.HashPath, token);
            Assert.AreEqual(IocSourceAvailability.Stale, stale.Availability);
            Assert.IsNull(stale.Content);
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_RollsBackFilesAlreadySavedWhenArchiveFails() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                using var archive = SignatureArchiveTests.Archive(lateInvalidEntry: true);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            Assert.AreEqual(SignatureRefreshResult.Failed, await new SignatureRefresh(contexts, clients, TimeProvider.System).RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            Assert.AreEqual(0, await db.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await db.SignatureFiles.CountAsync(token));
            Assert.AreEqual(0, await db.IocEntries.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_RejectsChangedPolicyBeforeSavingDownloadedData() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                using var db = contexts.CreateDbContext();
                db.IntelligenceSources.Single().MaximumRecords = 1;
                db.SaveChanges();
                using var archive = SignatureArchiveTests.Archive();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            Assert.AreEqual(SignatureRefreshResult.Conflict, await new SignatureRefresh(contexts, clients, TimeProvider.System).RunDueAsync(token));
            await using var final = contexts.CreateDbContext();
            Assert.AreEqual(0, await final.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await final.SignatureFiles.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_CancellationLeavesNoPartialRevision() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                cancellation.Cancel();
                using var archive = SignatureArchiveTests.Archive();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) };
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() => new SignatureRefresh(contexts, clients, TimeProvider.System).RunDueAsync(cancellation.Token));
            await using var final = contexts.CreateDbContext();
            Assert.AreEqual(0, await final.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await final.IocEntries.CountAsync(token));
            Assert.IsNull((await final.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public Task Issues_CanPageBeyondFiftyThousandLines() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            await using var db = contexts.CreateDbContext();
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = SignatureRefresh.SchemaVersion, MappingVersion = SignatureRefresh.MappingVersion,
                LicenseEvidence = SignatureRefresh.LicenseEvidence, RecordCount = 1, Normalized = true };
            var file = new SignatureFile { RevisionId = revision.Id, Path = "iocs/keywords.txt", Kind = 0, Digest = new byte[32], Content = [1] };
            db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision); db.SignatureFiles.Add(file);
            await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [security].[IocEntries] ([SignatureFileRowId], [LineNumber], [Kind], [Reason])
                SELECT {file.RowId}, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'unsupported', 'unrecognized-ioc-format'
                FROM (SELECT TOP (50001) 1 AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS numbers
                """, token);
            var status = new IntelligenceStatus(contexts, TimeProvider.System);
            var page1000 = await status.GetIocIssuesAsync(source.Id, revision.Id, file.Path, 1000, token);
            var page1001 = await status.GetIocIssuesAsync(source.Id, revision.Id, file.Path, 1001, token);
            Assert.IsNotNull(page1000); Assert.IsTrue(page1000.HasMore);
            Assert.IsNotNull(page1001); Assert.HasCount(1, page1001.Items); Assert.AreEqual(50001, page1001.Items[0].LineNumber);
            Assert.IsFalse(page1001.HasMore);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    { public SecurityAnalysisDbContext CreateDbContext() => new(options); }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class TestAudit(bool fail) : IOperationAuditWriter<OperationAudit>
    {
        public int Count { get; private set; }
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
        {
            if (fail) throw new InvalidOperationException("Injected audit failure");
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request)); }
    }
}
