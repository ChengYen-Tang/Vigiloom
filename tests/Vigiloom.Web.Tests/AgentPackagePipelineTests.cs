using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;
using Vigiloom.UpdateSecurity.Tests;
using Vigiloom.Web.Tests.Fixtures;

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
            var archive = "signed package"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
            var manifest = Encoding.UTF8.GetBytes($$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{hash}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""");
            var digest = Convert.ToHexStringLower(SHA256.HashData(manifest));
            var catalog = Encoding.UTF8.GetBytes($$"""{"schema":1,"packages":[{"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{hash}}","manifestSha256":"{{digest}}","revoked":false}]}""");
            var repository = new TestRepository(signer, catalogue: catalog, additionalTargets: new Dictionary<string, byte[]> { [digest + ".json"] = manifest });
            using var publisher = new HttpClient(new PublisherHandler(repository, hash, archive));
            var transport = new PackageLibraryTransport(Path.Combine(path, "server-cache"), 1024, TimeProvider.System, publisher);
            using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => { services.RemoveAll<IPackageLibraryTransport>(); services.AddSingleton<IPackageLibraryTransport>(transport); }));
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
            Assert.AreEqual(VersionAssignmentResult.Saved, await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().SetAsync(agent.AgentId.Value, packageId, "", actor, token));
            var localTrust = Path.Combine(path, "updater-trust");
            await new LocalTrustStore(localTrust).ProvisionAsync(root, token);
            using var http = PingTestSite.Client(factory);
            var updater = new CentralPackageAcquirer(new Uri("https://localhost/"), localTrust, Path.Combine(path, "updater-cache"), 1024, TimeProvider.System, http);
            await using var result = await updater.AcquireAsync(digest, "1.0.0", "win-x64", "1.0.0", null, credential, token);
            using var output = new MemoryStream();
            await result.Archive.CopyToAsync(output, token);
            CollectionAssert.AreEqual(archive, output.ToArray());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }, context.CancellationToken);

    private sealed class PublisherHandler(TestRepository repository, string archiveHash, byte[] archive) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
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
}
