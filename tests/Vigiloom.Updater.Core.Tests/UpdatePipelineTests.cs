using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Vigiloom.Agent.Hosting;
using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;
using Vigiloom.UpdateSecurity.Tests;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class UpdatePipelineTests(TestContext context)
{
    [TestMethod]
    public async Task SignedAssignedPackageIsDownloadedAndAppliedOnce()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"pipeline-{Guid.NewGuid():N}");
        try
        {
            var previous = Path.Combine(root, "old");
            Directory.CreateDirectory(previous);
            File.Copy(typeof(AgentHost).Assembly.Location, Path.Combine(previous, "Vigiloom.Agent.dll"));
            await File.WriteAllTextAsync(Path.Combine(previous, "Vigiloom.Agent.exe"), "old", context.CancellationToken);
            using var zipBytes = new MemoryStream();
            using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("Vigiloom.Agent.exe").Open())) writer.Write("new");
            var archive = zipBytes.ToArray();
            var archiveHash = Convert.ToHexStringLower(SHA256.HashData(archive));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, version = "1.0.0", rid = "win-x64", size = archive.Length, sha256 = archiveHash,
                minimumUpdaterVersion = "1.0.0", minimumAgentVersion = (string?)null, maximumAgentVersion = (string?)null
            });
            var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var repository = new TestRepository(key, catalogue: manifest, targetName: manifestHash + ".json");
            var trust = Path.Combine(root, "trust");
            await new LocalTrustStore(trust).ProvisionAsync(repository.Root(1, key), context.CancellationToken);
            var request = new UpdateHandoff { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };
            var device = Guid.CreateVersion7();
            var target = new AgentVersionTarget(request.UpdateId, Guid.CreateVersion7(), "1.0.0", "win-x64", manifestHash,
                Convert.ToBase64String(new byte[8]));
            var handler = new CentralHandler(repository, archive, request, device, target);
            using var http = new HttpClient(handler);
            var journal = new UpdateJournal(Path.Combine(root, "state"));
            var runtime = new TestRuntime(previous);
            var clock = TimeProvider.System;
            var server = new Uri("https://example.test/");
            var processor = new UpdateRequestProcessor(new CentralAssignmentClient(http, clock),
                new CentralPackageAcquirer(server, trust, Path.Combine(root, "cache"), 1024 * 1024, clock, http),
                new UpdateTransaction(http, journal, runtime, clock), journal, runtime, server, device, root, "win-x64", "1.0.0");

            var result = await processor.ProcessAsync(request, context.CancellationToken);
            Assert.AreEqual(CurrentAssignmentStatus.Available, result.Assignment);
            Assert.AreEqual(UpdatePhase.Succeeded, result.State?.Phase);
            Assert.AreEqual("new", await File.ReadAllTextAsync(Path.Combine(result.State!.TargetDirectory, "Vigiloom.Agent.exe"), context.CancellationToken));
            CollectionAssert.AreEqual(new[] { "stop", "select", "start", "health" }, runtime.Actions);
            Assert.AreEqual(1, handler.PackageRequests);
            Assert.IsTrue(handler.AllAuthenticated);

            var repeated = await processor.ProcessAsync(request, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Succeeded, repeated.State?.Phase);
            Assert.AreEqual(1, handler.PackageRequests);
            Assert.HasCount(4, runtime.Actions);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class TestRuntime(string previous) : IAgentUpdateRuntime
    {
        public List<string> Actions { get; } = [];
        public Task<string> GetSelectedDirectoryAsync(CancellationToken token) => Task.FromResult(previous);
        public Task StopAsync(CancellationToken token) { Actions.Add("stop"); return Task.CompletedTask; }
        public Task SelectVersionAsync(string versionDirectory, CancellationToken token) { Actions.Add("select"); return Task.CompletedTask; }
        public Task StartAsync(CancellationToken token) { Actions.Add("start"); return Task.CompletedTask; }
        public Task<bool> WaitHealthyAsync(CancellationToken token) { Actions.Add("health"); return Task.FromResult(true); }
    }

    private sealed class CentralHandler(TestRepository repository, byte[] archive, UpdateHandoff request, Guid device,
        AgentVersionTarget target) : HttpMessageHandler
    {
        public int PackageRequests { get; private set; }
        public bool AllAuthenticated { get; private set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            AllAuthenticated &= message.Headers.Authorization is { Scheme: "Bearer" } authorization && authorization.Parameter == request.Credential;
            var path = message.RequestUri!.AbsolutePath;
            if (path.EndsWith("/identity", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = Guid.ParseExact(request.Credential.AsSpan(4, 36), "D"), DeviceId = device }) };
            if (path.EndsWith("/version-assignment", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(target) };
            if (path.Contains("/packages/", StringComparison.Ordinal))
            {
                PackageRequests++;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
            }
            var name = Path.GetFileName(path);
            byte[]? bytes;
            if (path.Contains("/targets/", StringComparison.Ordinal)) bytes = await repository.FetchTargetAsync(name, token);
            else if (name == "2.root.json") bytes = null;
            else bytes = await repository.FetchMetadataAsync(Path.GetFileNameWithoutExtension(name), cancellationToken: token);
            return bytes is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
