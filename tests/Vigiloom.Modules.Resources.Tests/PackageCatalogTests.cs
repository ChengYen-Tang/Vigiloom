using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Modules.Resources.Application.AgentPackages;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PackageCatalogTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task DownloadsAndListsPackageFromResourceDatabase() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "vigiloom-package-" + Guid.CreateVersion7().ToString("N"));
            try
            {
                var services = new ServiceCollection().AddDbContextFactory<ResourcesDbContext>(options =>
                    options.UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")));
                await using var provider = services.BuildServiceProvider();
                var contexts = provider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
                await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                var contents = Encoding.UTF8.GetBytes("agent package");
                var sha = Convert.ToHexStringLower(SHA256.HashData(contents));
                using var client = new HttpClient(new ReleaseHandler(contents, sha)) { BaseAddress = new Uri("https://api.github.com/") };
                var usage = new PackageUsage { InUse = true };
                var files = new PackageLibraryTransport(path);
                var catalog = new PackageCatalog(contexts, new GitHubAgentReleases(client), files, usage);
                Assert.IsTrue(await catalog.DownloadAsync("1.2.3", "win-x64", token));
                Assert.IsTrue(await catalog.DownloadAsync("1.2.3", "win-x64", token));
                var item = (await catalog.ListAsync(1, token)).Items.Single();
                Assert.AreEqual("1.2.3", item.Version);
                Assert.AreEqual(sha, item.Sha256);
                Assert.IsTrue(item.Cached);
                Assert.AreEqual(7, item.Id.Version);
                Assert.AreEqual(AgentPackageRemovalResult.InUse, await catalog.RemoveAsync(item.Id, token));
                usage.InUse = false;
                var device = new Device("Server", "server.example", true, DateTimeOffset.UtcNow);
                var agent = new RegisteredAgent { Id = Guid.CreateVersion7(), DeviceId = device.Id, Verifier = new byte[32], CreatedAt = DateTimeOffset.UtcNow };
                await using (var db = await contexts.CreateDbContextAsync(token))
                {
                    db.Devices.Add(device);
                    db.Agents.Add(agent);
                    db.AgentVersionAssignments.Add(new AgentVersionAssignment { AgentId = agent.Id, PackageId = item.Id,
                        UpdateId = Guid.CreateVersion7(), AssignedBy = Guid.CreateVersion7(), AssignedAt = DateTimeOffset.UtcNow });
                    await db.SaveChangesAsync(token);
                }
                Assert.AreEqual(AgentPackageRemovalResult.InUse, await catalog.RemoveAsync(item.Id, token));
                var assignments = new AgentAssignments(contexts, files, null!, TimeProvider.System);
                var access = new AgentPackageAccess(assignments, files, contexts);
                var target = await assignments.GetTargetAsync(agent.Id, token);
                Assert.IsNotNull(target);
                Assert.IsNotNull(await access.ReadManifestAsync(agent.Id, target.ManifestSha256 + ".json", token));
                Assert.IsNull(await access.OpenArchiveAsync(agent.Id, new string('f', 64), token));
                await using (var archive = await access.OpenArchiveAsync(agent.Id, sha, token))
                {
                    Assert.IsNotNull(archive);
                    using var copied = new MemoryStream();
                    await archive.CopyToAsync(copied, token);
                    CollectionAssert.AreEqual(contents, copied.ToArray());
                }
                await using (var db = await contexts.CreateDbContextAsync(token))
                {
                    db.AgentVersionAssignments.Remove(await db.AgentVersionAssignments.SingleAsync(a => a.AgentId == agent.Id, token));
                    await db.SaveChangesAsync(token);
                }
                Assert.AreEqual(AgentPackageRemovalResult.Removed, await catalog.RemoveAsync(item.Id, token));
                Assert.IsNull(await access.OpenArchiveAsync(agent.Id, sha, token));
                Assert.IsFalse((await catalog.ListAsync(1, token)).Items.Single().Cached);
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        }, TestContext.CancellationToken);

    private sealed class ReleaseHandler(byte[] contents, string hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var json = $$"""{"tag_name":"agent-v1.2.3","assets":[{"name":"agent-win-x64.zip","size":{{contents.Length}},"digest":"sha256:{{hash}}"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) });
        }
    }

    private sealed class PackageUsage : IPackageRuntimeUsage
    {
        public bool InUse { get; set; }
        public Task<bool> IsInstalledAsync(string version, string runtimeIdentifier, CancellationToken token) => Task.FromResult(InUse);
    }
}
