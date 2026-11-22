using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentPackageDownloadTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task OnlyCurrentAssignmentCanReadManifestAndArchive() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var archive = "test package"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var json = $$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{hash}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var transport = new FixtureTransport(bytes, archive);
        using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => { services.RemoveAll<IPackageLibraryTransport>(); services.AddSingleton<IPackageLibraryTransport>(transport); }));
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        var device = new Device("download target", "127.0.0.1", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
        var actor = Guid.CreateVersion7();
        var issued = await identity.IssueAsync(device.Id, actor, token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var agent = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        Assert.IsNotNull(agent.AgentId);
        var credential = $"vla_{agent.AgentId.Value:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        var packageId = Guid.CreateVersion7();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Packages.Add(new AgentPackage { Id = packageId, Version = "1.0.0", RuntimeIdentifier = "win-x64", Size = archive.Length,
                Sha256 = hash, ManifestSha256 = digest });
            await db.SaveChangesAsync(token);
        }
        var assignments = scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>();
        Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agent.AgentId.Value, packageId, "", actor, token));
        using var anonymous = PingTestSite.Client(factory);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/agent/update/targets/{digest}.json", token)).StatusCode);
        using var client = PingTestSite.Client(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/agent/update/targets/catalog.json", token)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/agent/update/targets/{new string('a', 64)}.json", token)).StatusCode);
        CollectionAssert.AreEqual(bytes, await client.GetByteArrayAsync($"/api/v1/agent/update/targets/{digest}.json", token));
        CollectionAssert.AreEqual(archive, await client.GetByteArrayAsync($"/api/v1/agent/update/packages/{hash}", token));
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/agent/update/packages/{new string('a', 64)}", token)).StatusCode);
        var otherDevice = new Device("other target", "127.0.0.2", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(otherDevice); await db.SaveChangesAsync(token); }
        var otherIssued = await identity.IssueAsync(otherDevice.Id, actor, token);
        var otherSecret = RandomNumberGenerator.GetBytes(32);
        var other = await identity.RedeemAsync(otherIssued.Secret, SHA256.HashData(otherSecret), token);
        Assert.IsNotNull(other.AgentId);
        using var otherClient = PingTestSite.Client(factory);
        var otherCredential = $"vla_{other.AgentId.Value:D}.{Convert.ToBase64String(otherSecret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherCredential);
        Assert.AreEqual(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/agent/update/targets/{digest}.json", token)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/agent/update/packages/{hash}", token)).StatusCode);
        transport.BeforeOpenReturns = async () =>
        {
            var current = await assignments.GetAsync(agent.AgentId.Value, token);
            Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agent.AgentId.Value, null, current!.Revision, actor, token));
        };
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/agent/update/packages/{hash}", token)).StatusCode);
        Assert.IsFalse(transport.LastStream!.CanRead);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/agent/update/targets/{digest}.json", token)).StatusCode);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RevokeAsync(agent.AgentId.Value, actor, token));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/agent/update/packages/{hash}", token)).StatusCode);
    }, context.CancellationToken);

    private sealed class FixtureTransport(byte[] manifest, byte[] archive) : IPackageLibraryTransport
    {
        public bool IsCached(string archiveHash, string manifestHash) => true;
        public Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> otherManifests, CancellationToken token) => throw new NotSupportedException();
        public Func<Task>? BeforeOpenReturns { get; set; }
        public MemoryStream? LastStream { get; private set; }
        public Task SaveAsync(string version, AgentReleaseAsset asset, string manifestHash, byte[] bytes, GitHubAgentReleases github, CancellationToken token) => Task.CompletedTask;
        public Task<byte[]?> ReadManifestAsync(string hash, CancellationToken token) => Task.FromResult<byte[]?>(manifest);
        public async Task<Stream?> OpenCachedAsync(string hash, long size, CancellationToken token)
        {
            LastStream = new MemoryStream(archive, writable: false);
            if (BeforeOpenReturns is not null) await BeforeOpenReturns();
            return LastStream;
        }
    }
}
