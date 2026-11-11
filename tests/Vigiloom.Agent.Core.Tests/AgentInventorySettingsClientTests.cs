using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentInventorySettingsClientTests(TestContext context)
{
    [TestMethod]
    public async Task Sync_RetainsLastGoodPolicyAndFencesDifferentOwners()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventorySettingsTests");
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
            var client = new AgentInventorySettingsClient(http, TimeProvider.System);
            var saved = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Saved, saved.Status);
            Assert.AreEqual(3600, saved.Settings!.Snapshot.Settings.ScanIntervalSeconds);
            handler.Mode = 1;
            Assert.AreEqual(AgentSettingsSyncStatus.Unchanged, (await client.SyncAsync(vault, server, token)).Status);
            handler.Mode = 2;
            Assert.AreEqual(AgentSettingsSyncStatus.Invalid, (await client.SyncAsync(vault, server, token)).Status);
            Assert.AreEqual(saved.Settings.Snapshot, (await client.LoadSavedAsync(vault, server, token)).Settings!.Snapshot);
            var store = new AgentInventorySettingsStore(vault, server, id);
            await Assert.ThrowsExactlyAsync<AgentSettingsConflictException>(() => store.SaveAsync(saved.Settings.Snapshot,
                saved.Settings.SaveToken, DateTimeOffset.UtcNow, token));
            var replacement = Guid.CreateVersion7();
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { AgentId = replacement, Server = server.AbsoluteUri,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ReadAsync(token));
            var replacementStore = new AgentInventorySettingsStore(vault, server, replacement);
            Assert.IsNull(await replacementStore.ReadAsync(token));
            var replacementSaved = await replacementStore.SaveAsync(InventorySettingsSnapshot.Create(new(7200)), null, DateTimeOffset.UtcNow, token);
            Assert.AreEqual(replacement, replacementSaved.AgentId);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PolicyHandler : HttpMessageHandler
    {
        public int Mode { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("/api/v1/agent/inventory-settings", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            var snapshot = InventorySettingsSnapshot.Create(new(3600));
            var response = Mode == 1 ? new HttpResponseMessage(HttpStatusCode.NotModified) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) };
            response.Headers.ETag = new EntityTagHeaderValue($"\"{(Mode == 2 ? "wrong" : snapshot.Revision)}\"");
            return Task.FromResult(response);
        }
    }
}
