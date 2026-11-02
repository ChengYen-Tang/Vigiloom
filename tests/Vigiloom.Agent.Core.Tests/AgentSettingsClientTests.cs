using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentSettingsClientTests(TestContext testContext)
{
    [TestMethod]
    public async Task Sync_SavesValidSettingsAndPreservesThemWhenAResponseIsInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsClientTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = Secret() }, token);
            var snapshot = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 60 });
            var handler = new SequenceHandler(snapshot);
            using var http = new HttpClient(handler);
            var client = new AgentSettingsClient(http, TimeProvider.System);
            var server = new Uri("https://example.test/");
            var first = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Saved, first.Status);
            Assert.AreEqual(snapshot, first.Settings!.Snapshot);
            var second = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Unchanged, second.Status);
            Assert.AreEqual(first.Settings.Snapshot, second.Settings!.Snapshot);
            Assert.AreEqual(first.Settings.AppliedAt, second.Settings.AppliedAt);
            Assert.AreNotEqual(first.Settings.SaveToken, second.Settings.SaveToken);
            var invalid = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Invalid, invalid.Status);
            Assert.AreEqual(second.Settings, await new AgentSettingsStore(vault, server, id).ReadAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Sync_TimeoutAndCallerCancellationDoNotReplaceLastGoodSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsClientTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = Secret() }, token);
            var server = new Uri("https://example.test/");
            var store = new AgentSettingsStore(vault, server, id);
            var previous = await store.SaveAsync(AgentSettingsSnapshot.Create(AgentSettings.Default), null, DateTimeOffset.UtcNow, token);
            var clock = new FakeTimeProvider();
            var handler = new StalledSettingsHandler(previous.Snapshot);
            using var http = new HttpClient(handler);
            var client = new AgentSettingsClient(http, clock);
            var pending = client.SyncAsync(vault, server, token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            clock.Advance(TimeSpan.FromSeconds(15));
            Assert.AreEqual(AgentSettingsSyncStatus.Unavailable, (await pending).Status);
            Assert.AreEqual(previous, await store.ReadAsync(token));
            var second = new StalledSettingsHandler(previous.Snapshot);
            using var secondHttp = new HttpClient(second);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancelled = new AgentSettingsClient(secondHttp, clock).SyncAsync(vault, server, cancellation.Token);
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
            Assert.AreEqual(previous, await store.ReadAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Sync_OversizedResponseDoesNotReplaceLastGoodSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsClientTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = Secret() }, token);
            var server = new Uri("https://example.test/");
            var store = new AgentSettingsStore(vault, server, id);
            var previous = await store.SaveAsync(AgentSettingsSnapshot.Create(AgentSettings.Default), null, DateTimeOffset.UtcNow, token);
            using var http = new HttpClient(new OversizedSettingsHandler(previous.Snapshot));
            var result = await new AgentSettingsClient(http, TimeProvider.System).SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Unavailable, result.Status);
            Assert.AreEqual(previous, await store.ReadAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Sync_UnchangedResponseFencesAnOlderChangedResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsClientTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = Secret() }, token);
            var server = new Uri("https://example.test/");
            var store = new AgentSettingsStore(vault, server, id);
            var a = AgentSettingsSnapshot.Create(AgentSettings.Default);
            var b = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 90 });
            await store.SaveAsync(a, null, DateTimeOffset.UtcNow, token);
            var handler = new ReorderedResponsesHandler(a, b);
            using var http = new HttpClient(handler);
            var older = new AgentSettingsClient(http, TimeProvider.System).SyncAsync(vault, server, token);
            await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var current = await new AgentSettingsClient(http, TimeProvider.System).SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Unchanged, current.Status);
            handler.ReleaseFirst.TrySetResult();
            var stale = await older;
            Assert.AreEqual(AgentSettingsSyncStatus.Conflict, stale.Status);
            Assert.AreEqual(a, (await store.ReadAsync(token))!.Snapshot);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Sync_ConditionalResponseDoesNotReturnAnObsoleteLocalSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsClientTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = Secret() }, token);
            var server = new Uri("https://example.test/");
            var store = new AgentSettingsStore(vault, server, id);
            var first = await store.SaveAsync(AgentSettingsSnapshot.Create(AgentSettings.Default), null, DateTimeOffset.UtcNow, token);
            var newer = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 90 });
            using var http = new HttpClient(new ChangedDuringRequestHandler(store, first, newer));
            var result = await new AgentSettingsClient(http, TimeProvider.System).SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Conflict, result.Status);
            Assert.AreEqual(newer, result.Settings!.Snapshot);
        }
        finally { Cleanup(root); }
    }

    private sealed class OversizedSettingsHandler(AgentSettingsSnapshot previous) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4097]) };
            response.Headers.ETag = new EntityTagHeaderValue($"\"{previous.Revision}\"");
            return Task.FromResult(response);
        }
    }

    private sealed class StalledSettingsHandler(AgentSettingsSnapshot previous) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent(Started) };
            response.Headers.ETag = new EntityTagHeaderValue($"\"{previous.Revision}\"");
            return Task.FromResult(response);
        }
    }

    private sealed class StalledContent(TaskCompletionSource started) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.InfiniteTimeSpan);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class ReorderedResponsesHandler(AgentSettingsSnapshot current, AgentSettingsSnapshot older) : HttpMessageHandler
    {
        private int count;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(token);
                var changed = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(older) };
                changed.Headers.ETag = new EntityTagHeaderValue($"\"{older.Revision}\"");
                return changed;
            }
            var unchanged = new HttpResponseMessage(HttpStatusCode.NotModified);
            unchanged.Headers.ETag = new EntityTagHeaderValue($"\"{current.Revision}\"");
            return unchanged;
        }
    }

    private sealed class SequenceHandler(AgentSettingsSnapshot snapshot) : HttpMessageHandler
    {
        private int count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("/api/v1/agent/settings", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            count++;
            if (count > 1) Assert.AreEqual($"\"{snapshot.Revision}\"", request.Headers.IfNoneMatch.Single().Tag);
            var response = new HttpResponseMessage(count == 2 ? HttpStatusCode.NotModified : HttpStatusCode.OK);
            response.Headers.ETag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            if (count != 2) response.Content = JsonContent.Create(count == 1 ? snapshot : snapshot with { SchemaVersion = 2 });
            return Task.FromResult(response);
        }
    }

    private sealed class ChangedDuringRequestHandler(AgentSettingsStore store, AgentAppliedSettings previous, AgentSettingsSnapshot newer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await store.SaveAsync(newer, previous.SaveToken, DateTimeOffset.UtcNow, token);
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag = new EntityTagHeaderValue($"\"{previous.Snapshot.Revision}\"");
            return response;
        }
    }

    private static string Secret() => Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Cleanup(string root)
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomSettingsClientTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
