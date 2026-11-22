using System.Net;
using System.Net.Http.Json;
using Vigiloom.Agent.Hosting;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Updates;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class UpdateRequestProcessorTests(TestContext context)
{
    [TestMethod]
    public async Task FailedRecoveryRetainsSelectedBrokenAgentVersionAcrossRestart()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"failed-versions-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        try
        {
            var selected = Path.Combine(root, "broken");
            var target = Path.Combine(root, "next");
            Directory.CreateDirectory(selected);
            var now = DateTimeOffset.UtcNow;
            var journal = new UpdateJournal(Path.Combine(root, "state"));
            var previousFailure = new UpdateState(Guid.CreateVersion7(), selected, target, UpdatePhase.Failed, now,
                [new(UpdatePhase.Failed, now, "Interrupted")], PreviousAgentVersion: "1.2.0");
            await journal.WriteAsync(previousFailure, context.CancellationToken);
            var restarted = await new UpdateJournal(Path.Combine(root, "state")).ReadAsync(context.CancellationToken);
            Assert.IsNotNull(restarted);
            Assert.AreEqual("1.2.0", UpdateRequestProcessor.ReadCurrentVersion(selected, restarted));
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => UpdateRequestProcessor.ReadCurrentVersion(Path.Combine(root, "unrelated"), restarted));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task UnauthorizedHandoffNeverReadsOrStopsAgent()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"processor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient(new UnauthorizedHandler());
            var runtime = new TestRuntime(Path.Combine(root, "old"));
            var processor = Processor(http, runtime, root, Guid.CreateVersion7());
            var result = await processor.ProcessAsync(Request(), context.CancellationToken);
            Assert.AreEqual(CurrentAssignmentStatus.Unauthorized, result.Assignment);
            Assert.IsNull(result.State);
            Assert.AreEqual(0, runtime.Reads);
            Assert.AreEqual(0, runtime.Stops);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task IncompatibleSignedManifestNeverDownloadsOrStopsAgent()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"processor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var previous = Path.Combine(root, "old");
            Directory.CreateDirectory(previous);
            File.Copy(typeof(AgentHost).Assembly.Location, Path.Combine(previous, "Vigiloom.Agent.dll"));
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PythonTuf");
            await new LocalTrustStore(Path.Combine(root, "trust")).ProvisionAsync(
                await File.ReadAllBytesAsync(Path.Combine(fixture, "root.json"), context.CancellationToken), context.CancellationToken);
            var request = Request();
            var device = Guid.CreateVersion7();
            var target = new AgentVersionTarget(request.UpdateId, Guid.CreateVersion7(), "1.0.0", "linux-x64",
                "50abf0641e53ea3c0a39aa72c37e4d2e360e7b890abaff62537d3d3613d0e1d1", Convert.ToBase64String(new byte[8]));
            var handler = new FixtureHandler(fixture, request, device, target);
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(previous);
            var processor = Processor(http, runtime, root, device);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => processor.ProcessAsync(request, context.CancellationToken));
            Assert.AreEqual(1, runtime.Reads);
            Assert.AreEqual(0, runtime.Stops);
            Assert.IsFalse(handler.PackageRequested);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task SuccessfulAssignmentIsNotDownloadedOrAppliedAgain()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"processor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var request = Request();
            var device = Guid.CreateVersion7();
            var target = new AgentVersionTarget(request.UpdateId, Guid.CreateVersion7(), "1.0.0", "linux-x64",
                "50abf0641e53ea3c0a39aa72c37e4d2e360e7b890abaff62537d3d3613d0e1d1", Convert.ToBase64String(new byte[8]));
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PythonTuf");
            var handler = new FixtureHandler(fixture, request, device, target);
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(Path.Combine(root, "new"));
            var journal = new UpdateJournal(Path.Combine(root, "state"));
            var now = DateTimeOffset.UtcNow;
            await journal.WriteAsync(new UpdateState(request.UpdateId, Path.Combine(root, "old"), Path.Combine(root, "new"),
                UpdatePhase.Succeeded, now, [new(UpdatePhase.Succeeded, now, null)]), context.CancellationToken);
            var result = await Processor(http, runtime, root, device).ProcessAsync(request, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Succeeded, result.State!.Phase);
            Assert.AreEqual(0, runtime.Reads);
            Assert.AreEqual(0, runtime.Stops);
            Assert.IsFalse(handler.PackageRequested);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static UpdateRequestProcessor Processor(HttpClient http, TestRuntime runtime, string root, Guid device) => new(
        new CentralAssignmentClient(http, TimeProvider.System),
        new CentralPackageAcquirer(new Uri("https://example.test/"), Path.Combine(root, "trust"), Path.Combine(root, "cache"), 1024, TimeProvider.System, http),
        new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System),
        new UpdateJournal(Path.Combine(root, "state")), runtime,
        new Uri("https://example.test/"), device, root, "linux-x64", "1.0.0");

    private static UpdateHandoff Request() => new()
    { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };

    private sealed class TestRuntime(string previous) : IAgentUpdateRuntime
    {
        public int Reads { get; private set; }
        public int Stops { get; private set; }
        public Task<string> GetSelectedDirectoryAsync(CancellationToken token) { Reads++; return Task.FromResult(previous); }
        public Task StopAsync(CancellationToken token) { Stops++; return Task.CompletedTask; }
        public Task SelectVersionAsync(string versionDirectory, CancellationToken token) => Task.CompletedTask;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<bool> WaitHealthyAsync(CancellationToken token) => Task.FromResult(true);
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private sealed class FixtureHandler(string fixture, UpdateHandoff handoff, Guid device, AgentVersionTarget target) : HttpMessageHandler
    {
        public bool PackageRequested { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/identity", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = Guid.ParseExact(handoff.Credential.AsSpan(4, 36), "D"), DeviceId = device }) };
            if (path.EndsWith("/version-assignment", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(target) };
            if (path.Contains("/packages/", StringComparison.Ordinal)) { PackageRequested = true; return new(HttpStatusCode.NotFound); }
            var name = path.Contains("/targets/", StringComparison.Ordinal) ? "manifest.json" : Path.GetFileName(path);
            var file = Path.Combine(fixture, name);
            return File.Exists(file) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(file, token)) }
                : new(HttpStatusCode.NotFound);
        }
    }
}
