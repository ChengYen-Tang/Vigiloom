using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Updater.Core.Updates;
using Vigiloom.UpdateSecurity;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentUpdateAuthorizationTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task VerifierTracksRealAssignmentCancellationAndRevocation() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        var device = new Device("Agent test", "127.0.0.1", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
        var assignments = scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>();
        var actor = Guid.CreateVersion7();
        var issued = await identity.IssueAsync(device.Id, actor, token);
        Assert.AreEqual(AgentIdentityStatus.Success, issued.Status);
        var secret = RandomNumberGenerator.GetBytes(32);
        var agent = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        Assert.AreEqual(AgentIdentityStatus.Success, agent.Status);
        Assert.IsNotNull(agent.AgentId);
        var credential = $"vla_{agent.AgentId.Value:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

        // The real TUF signature path is verified separately; this test isolates central authorization.
        var now = DateTimeOffset.UtcNow;
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("Vigiloom.Agent.exe").Open())) writer.Write("test version");
        var digest = Convert.ToHexStringLower(SHA256.HashData(archive.ToArray()));
        var json = $$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{digest}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""";
        var verified = new VerifiedManifest(Encoding.UTF8.GetBytes(json), now.AddHours(1));
        var manifest = PackageManifest.Parse(verified, now);
        var packageId = Guid.CreateVersion7();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Packages.Add(new AgentPackage { Id = packageId, Version = manifest.Version, RuntimeIdentifier = manifest.RuntimeIdentifier,
                Sha256 = manifest.Sha256, ManifestSha256 = manifest.ManifestSha256, Size = manifest.Size, Listed = true, VerifiedUntil = now.AddHours(1) });
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agent.AgentId.Value, packageId, "", actor, token));
        using var http = PingTestSite.Client(factory);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var target = await http.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
        Assert.IsNotNull(target);
        var verifier = new AssignmentVerifier(http, new Uri("https://localhost/"), credential, target, manifest, TimeProvider.System);
        Assert.AreEqual(UpdateAuthorization.Authorized, await verifier.CheckAsync(target.UpdateId, token));
        var handoff = new UpdateHandoff { UpdateId = target.UpdateId, Credential = credential };
        var central = new CentralAssignmentClient(http, TimeProvider.System);
        var currentTarget = await central.GetAsync(new Uri("https://localhost/"), device.Id, handoff, token);
        Assert.AreEqual(CurrentAssignmentStatus.Available, currentTarget.Status);
        Assert.AreEqual(target, currentTarget.Target);
        Assert.AreEqual(CurrentAssignmentStatus.WrongDevice, (await central.GetAsync(new Uri("https://localhost/"), Guid.CreateVersion7(), handoff, token)).Status);

        var root = Path.Combine(context.TestRunDirectory!, $"update-integration-{Guid.NewGuid():N}");
        try
        {
            var previous = Path.Combine(root, "old");
            Directory.CreateDirectory(previous);
            await File.WriteAllTextAsync(Path.Combine(previous, "Vigiloom.Agent.exe"), "old version", token);
            VersionAssignmentResult? cancellation = null;
            var runtime = new TestRuntime(previous, async () =>
            {
                var assignment = await assignments.GetAsync(agent.AgentId.Value, token);
                if (assignment is not null)
                    cancellation = await assignments.SetAsync(agent.AgentId.Value, null, assignment.Revision, actor, token);
            });
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            var stopped = await transaction.ApplyAsync(new Uri("https://localhost/"), credential, target, verified, archive,
                root, previous, "win-x64", "1.0.0", "1.0.0", token);
            Assert.AreEqual(UpdatePhase.RolledBack, stopped.Phase);
            Assert.AreEqual(VersionAssignmentResult.Saved, cancellation);
            var cancelled = await assignments.GetAsync(agent.AgentId.Value, token);
            Assert.IsNotNull(cancelled);
            Assert.IsNull(cancelled.PackageId);
            Assert.AreEqual(previous, runtime.Selected);
            CollectionAssert.AreEqual(new[] { previous }, runtime.Selections);
            Assert.IsFalse(Directory.Exists(stopped.TargetDirectory));
            Assert.AreEqual("old version", await File.ReadAllTextAsync(Path.Combine(previous, "Vigiloom.Agent.exe"), token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        var view = await assignments.GetAsync(agent.AgentId.Value, token);
        Assert.IsNotNull(view);
        Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agent.AgentId.Value, packageId, view.Revision, actor, token));
        Assert.AreEqual(UpdateAuthorization.NoLongerAuthorized, await verifier.CheckAsync(target.UpdateId, token));
        Assert.AreEqual(CurrentAssignmentStatus.Changed, (await central.GetAsync(new Uri("https://localhost/"), device.Id, handoff, token)).Status);
        var reassigned = await http.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
        Assert.IsNotNull(reassigned);
        var current = new AssignmentVerifier(http, new Uri("https://localhost/"), credential, reassigned, manifest, TimeProvider.System);
        Assert.AreEqual(UpdateAuthorization.Authorized, await current.CheckAsync(reassigned.UpdateId, token));

        view = await assignments.GetAsync(agent.AgentId.Value, token);
        Assert.IsNotNull(view);
        Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agent.AgentId.Value, null, view.Revision, actor, token));
        Assert.AreEqual(UpdateAuthorization.NoLongerAuthorized, await current.CheckAsync(reassigned.UpdateId, token));
        view = await assignments.GetAsync(agent.AgentId.Value, token);
        Assert.IsNotNull(view);
        Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agent.AgentId.Value, packageId, view.Revision, actor, token));
        var again = await http.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
        Assert.IsNotNull(again);
        var final = new AssignmentVerifier(http, new Uri("https://localhost/"), credential, again, manifest, TimeProvider.System);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            var package = await db.Packages.SingleAsync(x => x.Id == packageId, token);
            package.Revoked = true;
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(UpdateAuthorization.NoLongerAuthorized, await final.CheckAsync(again.UpdateId, token));
        await identity.RevokeAsync(agent.AgentId.Value, actor, token);
        Assert.AreEqual(UpdateAuthorization.Unavailable, await final.CheckAsync(again.UpdateId, token));
        Assert.AreEqual(CurrentAssignmentStatus.Unauthorized, (await central.GetAsync(new Uri("https://localhost/"), device.Id, handoff, token)).Status);
    }, context.CancellationToken);

    private sealed class TestRuntime(string previous, Func<Task> cancelAssignment) : IAgentUpdateRuntime
    {
        private bool stopped;
        public string? Selected { get; private set; }
        public List<string> Selections { get; } = [];
        public Task<string> GetSelectedDirectoryAsync(CancellationToken token) => Task.FromResult(Selected ?? previous);
        public async Task StopAsync(CancellationToken token)
        {
            if (stopped) return;
            stopped = true;
            await cancelAssignment();
        }
        public Task SelectVersionAsync(string directory, CancellationToken token) { Selected = directory; Selections.Add(directory); return Task.CompletedTask; }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<bool> WaitHealthyAsync(CancellationToken token) => Task.FromResult(true);
    }
}
