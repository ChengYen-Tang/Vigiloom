using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Updates;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentVersionClientTests(TestContext context)
{
    [TestMethod]
    public async Task TimeoutCancellationAndOversizedResponseDoNotProduceTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vigiloom-version-test-{Guid.NewGuid():N}");
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = Guid.CreateVersion7(), MachineSecret = secret }, token);
            var server = new Uri("https://example.test/");
            var clock = new FakeTimeProvider();
            var stalled = new StalledHandler();
            using var http = new HttpClient(stalled);
            var pending = new AgentVersionClient(http, clock).GetAsync(vault, server, token);
            await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            clock.Advance(TimeSpan.FromSeconds(15));
            Assert.AreEqual(AgentVersionFetchStatus.Unavailable, (await pending).Status);

            var cancelledHandler = new StalledHandler();
            using var cancelledHttp = new HttpClient(cancelledHandler);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancelled = new AgentVersionClient(cancelledHttp, clock).GetAsync(vault, server, cancellation.Token);
            await cancelledHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);

            using var oversizedHttp = new HttpClient(new OversizedHandler());
            Assert.AreEqual(AgentVersionFetchStatus.Unavailable, (await new AgentVersionClient(oversizedHttp, clock).GetAsync(vault, server, token)).Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ReadsOnlyValidTargetAndRecognizesCancellationAndRevocation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vigiloom-version-test-{Guid.NewGuid():N}");
        try
        {
            var token = context.CancellationToken;
            var agentId = Guid.CreateVersion7();
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = agentId, MachineSecret = secret }, token);
            var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), "1.0.0", "win-x64",
                new string('a', 64), Convert.ToBase64String(new byte[8]));
            var handler = new SequenceHandler(target);
            using var http = new HttpClient(handler);
            var client = new AgentVersionClient(http, TimeProvider.System);
            var server = new Uri("https://example.test/");
            var first = await client.GetAsync(vault, server, token);
            Assert.AreEqual(AgentVersionFetchStatus.Available, first.Status);
            Assert.AreEqual(target, first.Target);
            Assert.AreEqual($"vla_{agentId:D}.{secret}", handler.Credential);
            var handoff = await client.CreateHandoffAsync(vault, server, first, token);
            Assert.IsNotNull(handoff);
            Assert.AreEqual(target.UpdateId, handoff.UpdateId);
            Assert.AreEqual($"vla_{agentId:D}.{secret}", handoff.Credential);
            Assert.DoesNotContain(secret, handoff.ToString());
            Assert.IsNull(await client.CreateHandoffAsync(vault, new Uri("https://other.example/"), first, token));
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = Guid.CreateVersion7(), MachineSecret = secret }, token);
            Assert.IsNull(await client.CreateHandoffAsync(vault, server, first, token));
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agentId, MachineSecret = secret }, token);
            Assert.AreEqual(AgentVersionFetchStatus.NoAssignment, (await client.GetAsync(vault, server, token)).Status);
            Assert.AreEqual(AgentVersionFetchStatus.Unauthorized, (await client.GetAsync(vault, server, token)).Status);
            Assert.AreEqual(AgentVersionFetchStatus.Invalid, (await client.GetAsync(vault, server, token)).Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) });
    }

    private sealed class SequenceHandler(AgentVersionTarget target) : HttpMessageHandler
    {
        private int count;
        public string? Credential { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credential = request.Headers.Authorization?.Parameter;
            Assert.AreEqual("/api/v1/agent/version-assignment", request.RequestUri!.AbsolutePath);
            return Task.FromResult(++count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target) },
                2 => new HttpResponseMessage(HttpStatusCode.NoContent),
                3 => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target with { UpdateId = Guid.Empty }) }
            });
        }
    }
}
