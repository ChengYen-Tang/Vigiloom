using System.Net;
using System.Net.Http.Json;
using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Updates;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class AssignmentVerifierTests(TestContext context)
{
    [TestMethod]
    public async Task ChecksAuthenticatedAssignmentAndDistinguishesUnavailableFromChanged()
    {
        var manifest = Manifest();
        var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest.Version, manifest.RuntimeIdentifier,
            manifest.ManifestSha256, Convert.ToBase64String(new byte[8]));
        var handler = new SequenceHandler(target);
        using var http = new HttpClient(handler);
        var verifier = new AssignmentVerifier(http, new Uri("https://vigiloom.example/"), "vla_test", target, manifest, TimeProvider.System);

        Assert.AreEqual(UpdateAuthorization.Authorized, await verifier.CheckAsync(target.UpdateId, context.CancellationToken));
        Assert.AreEqual(UpdateAuthorization.NoLongerAuthorized, await verifier.CheckAsync(target.UpdateId, context.CancellationToken));
        Assert.AreEqual(UpdateAuthorization.NoLongerAuthorized, await verifier.CheckAsync(target.UpdateId, context.CancellationToken));
        Assert.AreEqual(UpdateAuthorization.Unavailable, await verifier.CheckAsync(target.UpdateId, context.CancellationToken));
        Assert.AreEqual(UpdateAuthorization.Unavailable, await verifier.CheckAsync(target.UpdateId, context.CancellationToken));
        Assert.AreEqual(UpdateAuthorization.NoLongerAuthorized, await verifier.CheckAsync(Guid.CreateVersion7(), context.CancellationToken));
        Assert.AreEqual(5, handler.Calls);
    }

    [TestMethod]
    public void RejectsWrongManifestAndInsecureServer()
    {
        var manifest = Manifest();
        var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest.Version, manifest.RuntimeIdentifier,
            manifest.ManifestSha256, Convert.ToBase64String(new byte[8]));
        using var http = new HttpClient();
        Assert.ThrowsExactly<ArgumentException>(() => new AssignmentVerifier(http, new Uri("http://vigiloom.example/"), "token", target, manifest, TimeProvider.System));
        Assert.ThrowsExactly<ArgumentException>(() => new AssignmentVerifier(http, new Uri("https://vigiloom.example/"), "token",
            target with { PackageVersion = "2.0.0" }, manifest, TimeProvider.System));
    }

    [TestMethod]
    public async Task CoordinatorRestoresPreviousVersionWhenCentralAssignmentChangesDuringStop()
    {
        var manifest = Manifest();
        var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), manifest.Version, manifest.RuntimeIdentifier,
            manifest.ManifestSha256, Convert.ToBase64String(new byte[8]));
        var handler = new SequenceHandler(target);
        using var http = new HttpClient(handler);
        var verifier = new AssignmentVerifier(http, new Uri("https://vigiloom.example/"), "vla_test", target, manifest, TimeProvider.System);
        var runtime = new TestRuntime();
        var result = await new UpdateCoordinator(new MemoryJournal(), runtime).ApplyAsync(target.UpdateId, "old", "new", verifier.CheckAsync, context.CancellationToken);
        Assert.AreEqual(UpdatePhase.RolledBack, result.Phase);
        Assert.AreEqual("old", runtime.Selected);
        Assert.AreEqual(2, handler.Calls);
    }

    private static PackageManifest Manifest()
    {
        var json = $$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":1,"sha256":"{{new string('a', 64)}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""";
        return PackageManifest.Parse(new VerifiedManifest(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow.AddMinutes(5)), DateTimeOffset.UtcNow);
    }

    private sealed class SequenceHandler(AgentVersionTarget target) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("https://vigiloom.example/api/v1/agent/version-assignment", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("vla_test", request.Headers.Authorization.Parameter);
            return Task.FromResult(++Calls switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target) },
                2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target with { UpdateId = Guid.CreateVersion7() }) },
                3 => new HttpResponseMessage(HttpStatusCode.NoContent),
                4 => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) }
            });
        }
    }

    private sealed class TestRuntime : IAgentUpdateRuntime
    {
        public Task<string> GetSelectedDirectoryAsync(CancellationToken token) => Task.FromResult(Selected ?? "old");
        public string? Selected { get; private set; }
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public Task SelectVersionAsync(string versionDirectory, CancellationToken token) { Selected = versionDirectory; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<bool> WaitHealthyAsync(CancellationToken token) => Task.FromResult(true);
    }

    private sealed class MemoryJournal : IUpdateJournal
    {
        private UpdateState? state;
        public Task<UpdateState?> ReadAsync(CancellationToken token) => Task.FromResult(state);
        public Task WriteAsync(UpdateState value, CancellationToken token) { state = value; return Task.CompletedTask; }
    }
}
