using System.Data.Common;
using System.IO.Compression;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class BaselineSourceTests(TestContext context)
{
    [TestMethod]
    [DataRow("compliance-as-code-shadow", "shadow-rule.yml.gz", "file_permissions_etc_shadow", "0640-or-stricter")]
    [DataRow("powerstig-network-selection", "windows-2022.xml.gz", "V-254348", "1")]
    public Task ReviewedSource_IsStagedPublishedAndRemainsAvailableAfterFailedRefresh(string key, string file, string expectedId, string expectedValue) =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var definition = BaselineDefinitions.Find(key)!;
            await using var fixture = typeof(BaselineSourceTests).Assembly.GetManifestResourceStream(
                "Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence.Fixtures.Baseline." + file);
            Assert.IsNotNull(fixture);
            using var buffer = new MemoryStream();
            if (file.EndsWith(".gz", StringComparison.Ordinal))
            {
                await using var decompressed = new GZipStream(fixture, CompressionMode.Decompress);
                await decompressed.CopyToAsync(buffer, token);
            }
            else await fixture.CopyToAsync(buffer, token);
            var client = new ClientFactory(buffer.ToArray());
            var refresh = new BaselineRefresh(contexts, client, TimeProvider.System);
            Assert.AreEqual(BaselineRefreshResult.Staged, await refresh.RunDueAsync(definition.Key, token));
            var source = new BaselineSource(contexts, TimeProvider.System);
            Assert.AreEqual(BaselineSourceAvailability.NoActiveRevision, (await source.GetActiveAsync(definition.Key, token)).Availability);
            await using var db = contexts.CreateDbContext();
            var row = await db.IntelligenceSources.SingleAsync(token);
            var revision = await db.IntelligenceRevisions.SingleAsync(token);
            var publication = new IntelligencePublication(contexts, new NoopAudit(), TimeProvider.System);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publication.ActivateAsync(row.Id, revision.Id, null, "", false, Guid.CreateVersion7(), token));
            var selected = await source.GetActiveAsync(definition.Key, token);
            Assert.AreEqual(BaselineSourceAvailability.Ready, selected.Availability);
            Assert.AreEqual(revision.Id, selected.RevisionId);
            Assert.AreEqual(expectedId, selected.Check!.Id);
            Assert.AreEqual(expectedValue, selected.Check.Expected);

            row.LastCheckedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync(token);
            client.Content = [1, 2, 3];
            Assert.AreEqual(BaselineRefreshResult.Failed, await refresh.RunDueAsync(definition.Key, token));
            Assert.AreEqual(BaselineSourceAvailability.Ready, (await source.GetActiveAsync(definition.Key, token)).Availability);
            Assert.AreEqual(1, await db.IntelligenceRevisions.CountAsync(token));
            var view = (await new IntelligenceStatus(contexts, TimeProvider.System).GetSourcesAsync(1, token)).Items.Single();
            Assert.IsNotNull(view.LastError);
            Assert.AreEqual("security-configuration-assessment", view.Description!.Category);
        }, context.CancellationToken);

    [TestMethod]
    public Task ModifiedArtifact_CannotBePublished() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var definition = BaselineDefinitions.Find("powerstig-network-selection")!;
            var source = new Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence.IntelligenceSource
                { Key = definition.Key, Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            var revision = new Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence.IntelligenceRevision
            {
                SourceId = source.Id, UpstreamVersion = definition.Commit, RetrievedAt = DateTimeOffset.UtcNow,
                Artifact = [1], Digest = System.Security.Cryptography.SHA256.HashData([1]), SchemaVersion = BaselineDefinitions.Schema,
                MappingVersion = BaselineDefinitions.Mapping, LicenseEvidence = definition.LicenseEvidence, RecordCount = 1, Normalized = true
            };
            await using var db = contexts.CreateDbContext();
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            var publication = new IntelligencePublication(contexts, new NoopAudit(), TimeProvider.System);
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publication.ActivateAsync(source.Id, revision.Id, null, "", false, Guid.CreateVersion7(), token));
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
    private sealed class ClientFactory(byte[] content) : IHttpClientFactory
    {
        public byte[] Content { get; set; } = content;
        public HttpClient CreateClient(string name) => new(new Handler(this));
    }
    private sealed class Handler(ClientFactory factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(factory.Content) });
    }
    private sealed class NoopAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
