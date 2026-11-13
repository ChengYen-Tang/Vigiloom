using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentIntegritySettingsClientTests(TestContext context)
{
    [TestMethod]
    public async Task DelayedNotModified_CannotRestoreOlderPolicy()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegritySettingsTests");
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
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, "/srv/old", true,
                ImmutableArray<string>.Empty, 3600, 1000, 1048576, 10485760, 16);
            var old = IntegritySettingsSnapshot.Create([scope], 1);
            var store = new AgentIntegritySettingsStore(vault, server, id);
            var saved = await store.SaveAsync(old, null, DateTimeOffset.UtcNow, token);
            var handler = new DelayedHandler(old.Revision);
            using var http = new HttpClient(handler);
            var client = new AgentIntegritySettingsClient(http, TimeProvider.System);
            var pending = client.SyncAsync(vault, server, token);
            await handler.Started.Task.WaitAsync(token);
            var newer = IntegritySettingsSnapshot.Create([scope with { Revision = 2, Root = "/srv/new" }], 2);
            await store.SaveAsync(newer, saved.SaveToken, DateTimeOffset.UtcNow, token);
            handler.Release.SetResult();
            var result = await pending;
            Assert.AreEqual(AgentSettingsSyncStatus.Conflict, result.Status);
            Assert.AreEqual(newer.Revision, result.Settings!.Snapshot.Revision);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Sync_RetainsLastGoodPolicyOnInvalidTagOrRevisionRollback()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegritySettingsTests");
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
            var client = new AgentIntegritySettingsClient(http, TimeProvider.System);
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
            var replacement = Guid.CreateVersion7();
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { AgentId = replacement, Server = server.AbsoluteUri,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var replacementStore = new AgentIntegritySettingsStore(vault, server, replacement);
            Assert.IsNull(await replacementStore.ReadAsync(token));
            handler.Mode = 0;
            var replacementResult = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Saved, replacementResult.Status);
            Assert.AreEqual(replacement, replacementResult.Settings!.AgentId);
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
        private readonly IntegrityScope scope = new(Guid.CreateVersion7(), 2, IntegrityScopeKind.Directory, "/srv/app", true,
            ImmutableArray.Create("logs/**"), 3600, 1000, 1048576, 10485760, 16);
        public int Mode { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("/api/v1/agent/integrity-settings", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            var snapshot = IntegritySettingsSnapshot.Create([Mode == 3 ? scope with { Revision = 1 } : scope], Mode == 3 ? 1 : 2);
            var response = Mode == 4 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) :
                new HttpResponseMessage(Mode == 1 ? HttpStatusCode.NotModified : HttpStatusCode.OK)
                { Content = Mode == 1 ? null : JsonContent.Create(snapshot) };
            if (Mode != 4) response.Headers.ETag = new EntityTagHeaderValue($"\"{(Mode == 2 ? "wrong" : snapshot.Revision)}\"");
            return Task.FromResult(response);
        }
    }

    private sealed class DelayedHandler(string revision) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(token);
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag = new EntityTagHeaderValue($"\"{revision}\"");
            return response;
        }
    }
}
