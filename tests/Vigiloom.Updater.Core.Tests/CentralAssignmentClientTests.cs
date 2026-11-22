using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class CentralAssignmentClientTests(TestContext context)
{
    [TestMethod]
    public async Task TimeoutAndCallerCancellationNeverAuthorizeUpdate()
    {
        var deviceId = Guid.CreateVersion7();
        var request = Request();
        var clock = new FakeTimeProvider();
        var stalled = new StalledHandler();
        using var http = new HttpClient(stalled);
        var client = new CentralAssignmentClient(http, clock);
        var pending = client.GetAsync(new Uri("https://example.test/"), deviceId, request, context.CancellationToken);
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), context.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.AreEqual(CurrentAssignmentStatus.Unavailable, (await pending).Status);

        var other = new StalledHandler();
        using var second = new HttpClient(other);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var cancelled = new CentralAssignmentClient(second, clock).GetAsync(new Uri("https://example.test/"), deviceId, request, cancellation.Token);
        await other.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), context.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    }

    [TestMethod]
    public async Task NoAssignmentAndUnauthorizedResponseAreNotAvailable()
    {
        var request = Request();
        var agent = Guid.ParseExact(request.Credential.AsSpan(4, 36), "D");
        var device = Guid.CreateVersion7();
        using var http = new HttpClient(new IdentityAndAssignmentHandler(agent, device));
        var client = new CentralAssignmentClient(http, TimeProvider.System);
        Assert.AreEqual(CurrentAssignmentStatus.Changed,
            (await client.GetAsync(new Uri("https://example.test/"), device, request, context.CancellationToken)).Status);
        Assert.AreEqual(CurrentAssignmentStatus.Unauthorized,
            (await client.GetAsync(new Uri("https://example.test/"), device, request, context.CancellationToken)).Status);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.GetAsync(new Uri("http://example.test/"), device, request, context.CancellationToken));
    }

    private static UpdateHandoff Request() => new()
    { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };

    [TestMethod]
    public async Task CurrentAssignmentRequiresMatchingDeviceAndReturnsCurrentTarget()
    {
        var request = Request();
        var agent = Guid.ParseExact(request.Credential.AsSpan(4, 36), "D");
        var device = Guid.CreateVersion7();
        var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), "1.0.0", "linux-x64",
            new string('a', 64), Convert.ToBase64String(new byte[8]));
        using var http = new HttpClient(new CurrentAssignmentHandler(agent, device, target));
        var client = new CentralAssignmentClient(http, TimeProvider.System);
        var server = new Uri("https://example.test/");

        var current = await client.GetCurrentAsync(server, device, request.Credential, context.CancellationToken);
        Assert.AreEqual(CurrentAssignmentStatus.Available, current.Status);
        Assert.AreEqual(target, current.Target);
        Assert.AreEqual(CurrentAssignmentStatus.Changed,
            (await client.GetAsync(server, device, request, context.CancellationToken)).Status);
        Assert.AreEqual(CurrentAssignmentStatus.WrongDevice,
            (await client.GetCurrentAsync(server, Guid.CreateVersion7(), request.Credential, context.CancellationToken)).Status);
        Assert.AreEqual(CurrentAssignmentStatus.Unauthorized,
            (await client.GetCurrentAsync(server, device, "invalid", context.CancellationToken)).Status);
    }

    private sealed class CurrentAssignmentHandler(Guid agent, Guid device, AgentVersionTarget target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/identity", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = agent, DeviceId = device }) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target) });
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class IdentityAndAssignmentHandler(Guid agent, Guid device) : HttpMessageHandler
    {
        private int calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(++calls switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = agent, DeviceId = device }) },
                2 => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            });
    }
}
