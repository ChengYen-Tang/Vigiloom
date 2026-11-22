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
using Vigiloom.Updater.Core.Updates;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentPackagePipelineTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task UpdaterDownloadsAssignedCachedPackage() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var path = Path.Combine(context.TestRunDirectory!, $"agent-package-{Guid.CreateVersion7():N}");
        try
        {
            using var archiveBuffer = new MemoryStream();
            using (var zip = new ZipArchive(archiveBuffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("Vigiloom.Agent.exe").Open())) writer.Write("new version");
            var archive = archiveBuffer.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
            using var packages = new SignedAgentPackages(archive, archive, "win-x64");
            using var github = packages.CreateClient();
            var transport = new PackageLibraryTransport(Path.Combine(path, "server-cache"));
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
            var library = scope.ServiceProvider.GetRequiredService<PackageLibrary>();
            Assert.IsTrue(await library.CacheAsync("1.0.0", "win-x64", token));
            // The central library must continue serving the assignment without GitHub.
            github.Dispose();
            Guid packageId;
            string digest;
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            {
                var packageRecord = await db.Packages.SingleAsync(token);
                packageId = packageRecord.Id;
                digest = packageRecord.ManifestSha256;
            }
            Assert.IsTrue(transport.IsCached(hash, digest));
            Assert.AreEqual(VersionAssignmentResult.Saved, await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().SetAsync(agent.AgentId.Value, packageId, "", actor, token));
            using var http = PingTestSite.Client(factory);
            var updater = new CentralPackageAcquirer(new Uri("https://localhost/"), Path.Combine(path, "updater-cache"), TimeProvider.System, http);
            await using (var package = await updater.AcquireAsync(digest, "1.0.0", "win-x64", credential, token))
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
            Assert.AreEqual(PackageRemovalResult.InUse, await library.RemoveAsync(packageId, token));
            var assignment = await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().GetAsync(agent.AgentId.Value, token);
            Assert.AreEqual(VersionAssignmentResult.Saved, await scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>().SetAsync(agent.AgentId.Value, null, assignment!.Revision, actor, token));
            Assert.AreEqual(PackageRemovalResult.Removed, await library.RemoveAsync(packageId, token));
            Assert.IsFalse(transport.IsCached(hash, digest));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }, context.CancellationToken);

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
