using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Agent.Hosting;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;
using Vigiloom.UpdateSecurity.Tests;
using Vigiloom.Updater.Core.Updates;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Web.AgentPackages;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentPackagePipelineTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task UpdaterDownloadsAssignedPublisherSignedPackage() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var path = Path.Combine(context.TestRunDirectory!, $"agent-package-{Guid.CreateVersion7():N}");
        try
        {
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var archiveBuffer = new MemoryStream();
            using (var zip = new ZipArchive(archiveBuffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("Vigiloom.Agent.exe").Open())) writer.Write("new version");
            var archive = archiveBuffer.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
            var manifest = Encoding.UTF8.GetBytes($$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{hash}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""");
            var digest = Convert.ToHexStringLower(SHA256.HashData(manifest));
            var catalog = Encoding.UTF8.GetBytes($$"""{"schema":1,"packages":[{"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{hash}}","manifestSha256":"{{digest}}","revoked":false}]}""");
            var repository = new TestRepository(signer, catalogue: catalog, additionalTargets: new Dictionary<string, byte[]> { [digest + ".json"] = manifest });
            var handler = new PublisherHandler(repository, hash, archive);
            using var publisher = new HttpClient(handler);
            using var github = new HttpClient(new ReleaseHandler()) { BaseAddress = new Uri("https://api.github.com/") };
            var transport = new PackageLibraryTransport(Path.Combine(path, "server-cache"), 1024, TimeProvider.System, publisher);
            using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPackageLibraryTransport>();
                    services.AddSingleton<IPackageLibraryTransport>(transport);
                    services.RemoveAll<GitHubAgentReleases>();
                    services.AddSingleton(new GitHubAgentReleases(github));
                }));
            await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var device = new Device("pipeline", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            using var scope = factory.Services.CreateScope();
            var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
            var actor = Guid.CreateVersion7();
            var issued = await identity.IssueAsync(device.Id, actor, token);
            var secret = RandomNumberGenerator.GetBytes(32);
            var agent = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
            Assert.IsNotNull(agent.AgentId);
            var credential = $"vla_{agent.AgentId.Value:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
            var source = await scope.ServiceProvider.GetRequiredService<AgentPackageSourceSettings>().UpdateAsync(new("https://publisher.test/", PackageCredentialAction.Clear, null, ""), actor, token);
            Assert.IsNotNull(source);
            var root = repository.Root(1, signer);
            var trust = scope.ServiceProvider.GetRequiredService<UpdateTrustStore>();
            await trust.ProvisionAsync(root, token);
            var origin = new Uri("https://publisher.test/");
            await trust.RefreshCatalogueAsync(new TrustedCatalogueReader(origin, origin, repository), TimeProvider.System, Convert.FromBase64String(source.Version), cancellationToken: token);
            Guid packageId;
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token)) packageId = (await db.Packages.SingleAsync(token)).Id;
            Assert.AreEqual(VersionAssignmentResult.Conflict, await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().SetAsync(agent.AgentId.Value, packageId, "", actor, token));
            Assert.IsNull((await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().GetAsync(agent.AgentId.Value, token))!.PackageId);
            using (var admin = PingTestSite.Client(factory))
            {
                await PingTestSite.LoginAsync(admin, "admin", token);
                using var page = await admin.GetAsync("/settings/agent-packages?releases=1", token);
                var html = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = html.QuerySelector($"form[action^='/settings/agent-packages/{packageId:D}/download']");
                Assert.IsNotNull(form);
                var anti = form.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
                using var response = await admin.PostAsync(form.GetAttribute("action"),
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = anti }), token);
                Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
                Assert.AreEqual("/settings/agent-packages?releases=1&download=saved", response.Headers.Location?.OriginalString);
            }
            Assert.IsTrue(transport.IsCached(hash, digest));
            handler.Offline = true;
            Assert.AreEqual(VersionAssignmentResult.Saved, await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().SetAsync(agent.AgentId.Value, packageId, "", actor, token));
            var localTrust = Path.Combine(path, "updater-trust");
            await new LocalTrustStore(localTrust).ProvisionAsync(root, token);
            using var http = PingTestSite.Client(factory);
            var updater = new CentralPackageAcquirer(new Uri("https://localhost/"), localTrust, Path.Combine(path, "updater-cache"), 1024, TimeProvider.System, http);
            await using (var package = await updater.AcquireAsync(digest, "1.0.0", "win-x64", "1.0.0", null, credential, token))
            {
                using var output = new MemoryStream();
                await package.Archive.CopyToAsync(output, token);
                CollectionAssert.AreEqual(archive, output.ToArray());
            }
            using var authorizedHttp = PingTestSite.Client(factory);
            authorizedHttp.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            var target = await authorizedHttp.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
            Assert.IsNotNull(target);
            var previous = Path.Combine(path, "old");
            Directory.CreateDirectory(previous);
            File.Copy(typeof(AgentHost).Assembly.Location, Path.Combine(previous, "Vigiloom.Agent.dll"));
            await File.WriteAllTextAsync(Path.Combine(previous, "Vigiloom.Agent.exe"), "old version", token);
            var runtime = new TestRuntime(previous);
            var journal = new UpdateJournal(Path.Combine(path, "state"));
            var server = new Uri("https://localhost/");
            var processor = new UpdateRequestProcessor(new CentralAssignmentClient(http, TimeProvider.System), updater,
                new UpdateTransaction(http, journal, runtime, TimeProvider.System), journal, runtime, server, device.Id,
                path, "win-x64", "1.0.0");
            var result = await processor.ProcessAsync(new UpdateHandoff { UpdateId = target.UpdateId, Credential = credential }, token);
            Assert.AreEqual(CurrentAssignmentStatus.Available, result.Assignment);
            Assert.AreEqual(UpdatePhase.Succeeded, result.State?.Phase);
            Assert.AreEqual("new version", await File.ReadAllTextAsync(Path.Combine(result.State!.TargetDirectory, "Vigiloom.Agent.exe"), token));
            var library = scope.ServiceProvider.GetRequiredService<PackageLibrary>();
            Assert.AreEqual(PackageRemovalResult.InUse, await library.RemoveAsync(packageId, token));
            var assignment = await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().GetAsync(agent.AgentId.Value, token);
            Assert.AreEqual(VersionAssignmentResult.Saved, await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().SetAsync(agent.AgentId.Value, null, assignment!.Revision, actor, token));
            Assert.AreEqual(PackageRemovalResult.Removed, await library.RemoveAsync(packageId, token));
            Assert.IsFalse(transport.IsCached(hash, digest));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }, context.CancellationToken);

    private sealed class PublisherHandler(TestRepository repository, string archiveHash, byte[] archive) : HttpMessageHandler
    {
        public bool Offline { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("Publisher is offline.");
            var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            byte[]? bytes = null;
            if (parts is ["packages", var hash] && hash == archiveHash) bytes = archive;
            else if (parts is ["targets", var target]) bytes = await repository.FetchTargetAsync(target, token);
            else if (parts is ["metadata", var name])
            {
                var fields = name.Split('.');
                var version = fields.Length == 3 && int.TryParse(fields[0], out var value) ? value : (int?)null;
                bytes = await repository.FetchMetadataAsync(fields[version is null ? 0 : 1], version, token);
            }
            return bytes is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[{\"tag_name\":\"agent-v1.0.0\",\"draft\":false,\"prerelease\":false}]", Encoding.UTF8, "application/json") });
    }

    private sealed class TestRuntime(string previous) : IAgentUpdateRuntime
    {
        private string selected = previous;
        public Task<string> GetSelectedDirectoryAsync(CancellationToken token) => Task.FromResult(selected);
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public Task SelectVersionAsync(string directory, CancellationToken token) { selected = directory; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<bool> WaitHealthyAsync(CancellationToken token) => Task.FromResult(true);
    }
}
