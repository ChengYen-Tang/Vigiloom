using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentLogSettingsClientTests(TestContext context)
{
    [TestMethod]
    public async Task Sync_RetainsLastGoodSnapshotWhenResponseOrTransitionIsInvalid()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomLogSettingsTests");
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
            var handler = new SourceHandler();
            using var http = new HttpClient(handler);
            var client = new AgentLogSettingsClient(http, TimeProvider.System);
            var first = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Saved, first.Status);
            Assert.IsNotNull(first.Settings);
            handler.Mode = 1;
            Assert.AreEqual(AgentSettingsSyncStatus.Unchanged, (await client.SyncAsync(vault, server, token)).Status);
            handler.Mode = 2;
            Assert.AreEqual(AgentSettingsSyncStatus.Invalid, (await client.SyncAsync(vault, server, token)).Status);
            handler.Mode = 3;
            Assert.AreEqual(AgentSettingsSyncStatus.Invalid, (await client.SyncAsync(vault, server, token)).Status);
            handler.Mode = 4;
            Assert.AreEqual(AgentSettingsSyncStatus.Unauthorized, (await client.SyncAsync(vault, server, token)).Status);
            var persisted = await client.LoadSavedAsync(vault, server, token);
            Assert.AreEqual(first.Settings.Snapshot.Revision, persisted.Settings!.Snapshot.Revision);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SourceHandler : HttpMessageHandler
    {
        private readonly AgentLogSourceSetting source = new(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", "local", true, 1, null);
        public int Mode { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("/api/v1/agent/log-settings", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            var snapshot = AgentLogSettingsSnapshot.Create([Mode == 3 ? source with { Enabled = false } : source]);
            var response = Mode == 4 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) :
                new HttpResponseMessage(Mode == 1 ? HttpStatusCode.NotModified : HttpStatusCode.OK)
                { Content = Mode == 1 ? null : JsonContent.Create(snapshot) };
            if (Mode != 4) response.Headers.ETag = new EntityTagHeaderValue($"\"{(Mode == 2 ? "wrong" : snapshot.Revision)}\"");
            return Task.FromResult(response);
        }
    }
}
