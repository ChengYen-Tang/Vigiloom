using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentQueueCapacityClientTests(TestContext context)
{
    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task UnchangedResponseFencesAnOlderChangedResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomCapacityClientTests", Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { AgentId = id, Server = server.AbsoluteUri,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var original = AgentQueueCapacitySnapshot.Create(AgentQueueCapacity.Default);
            var obsolete = AgentQueueCapacitySnapshot.Create(new(1024 * 1024, 100));
            var store = new AgentQueueCapacityStore(vault, server, id);
            var initial = await store.SaveAsync(original, null, DateTimeOffset.UtcNow, token);
            var handler = new ReorderedHandler(original, obsolete);
            using var http = new HttpClient(handler);
            var client = new AgentQueueCapacityClient(http, TimeProvider.System);
            var earlier = client.SyncAsync(vault, server, token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var current = await client.SyncAsync(vault, server, token);
            Assert.AreEqual(AgentSettingsSyncStatus.Unchanged, current.Status);
            Assert.AreNotEqual(initial.SaveToken, current.Settings!.SaveToken);
            handler.Release.TrySetResult();
            Assert.AreEqual(AgentSettingsSyncStatus.Conflict, (await earlier).Status);
            Assert.AreEqual(original, (await store.ReadAsync(token))!.Snapshot);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomCapacityClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ReorderedHandler(AgentQueueCapacitySnapshot original, AgentQueueCapacitySnapshot obsolete) : HttpMessageHandler
    {
        private int calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(obsolete) };
                response.Headers.ETag = new EntityTagHeaderValue($"\"{obsolete.Revision}\"");
                return response;
            }
            var unchanged = new HttpResponseMessage(HttpStatusCode.NotModified);
            unchanged.Headers.ETag = new EntityTagHeaderValue($"\"{original.Revision}\"");
            return unchanged;
        }
    }
}
