using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentMetricSettingsClientTests(TestContext context)
{
    [TestMethod]
    public async Task Sync_ValidatesEtagAndRetainsLastGoodSettings()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricSettingsTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { AgentId = id, Server = server.AbsoluteUri,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var handler = new PolicyHandler();
            using var http = new HttpClient(handler);
            var client = new AgentMetricSettingsClient(http, TimeProvider.System);
            var saved = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Saved, saved.Status);
            Assert.AreEqual(new AgentMetricSettings(45, 90, 1500, 768, 3500, 8, 96), saved.Settings!.Snapshot.Settings);
            handler.InvalidTag = true;
            Assert.AreEqual(AgentSettingsSyncStatus.Invalid, (await client.SyncAsync(vault, server, token)).Status);
            Assert.AreEqual(saved.Settings.Snapshot, (await client.LoadSavedAsync(vault, server, token)).Settings!.Snapshot);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PolicyHandler : HttpMessageHandler
    {
        public bool InvalidTag { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("/api/v1/agent/metric-settings", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            var snapshot = AgentMetricSettingsSnapshot.Create(new(45, 90, 1500, 768, 3500, 8, 96));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) };
            response.Headers.ETag = new EntityTagHeaderValue($"\"{(InvalidTag ? "wrong" : snapshot.Revision)}\"");
            return Task.FromResult(response);
        }
    }
}
