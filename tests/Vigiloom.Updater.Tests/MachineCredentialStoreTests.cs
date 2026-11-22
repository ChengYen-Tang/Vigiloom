using Vigiloom.Updater.Hosting;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Tests;

[TestClass]
public sealed class MachineCredentialStoreTests(TestContext context)
{
    [TestMethod]
    public async Task RetainsAndReplacesCredentialForIndependentUpdater()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"updater-credential-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MachineCredentialStore(directory);
            var first = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}";
            var rotated = $"vla_{Guid.CreateVersion7():D}.{new string('b', 43)}";
            Assert.IsNull(await store.ReadAsync(context.CancellationToken));
            await store.WriteAsync(first, context.CancellationToken);
            Assert.AreEqual(first, await new MachineCredentialStore(directory).ReadAsync(context.CancellationToken));
            await store.WriteAsync(rotated, context.CancellationToken);
            Assert.AreEqual(rotated, await store.ReadAsync(context.CancellationToken));
            Assert.HasCount(1, Directory.GetFiles(directory));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.WriteAsync("invalid", context.CancellationToken));
            Assert.AreEqual(rotated, await store.ReadAsync(context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task UpdaterPollsCurrentAssignmentWithoutAnAgentHandoff()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"updater-poll-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MachineCredentialStore(directory);
            var agent = Guid.CreateVersion7();
            var device = Guid.CreateVersion7();
            var credential = $"vla_{agent:D}.{new string('a', 43)}";
            var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), "1.0.0", "win-x64",
                new string('a', 64), Convert.ToBase64String(new byte[8]));
            await store.WriteAsync(credential, context.CancellationToken);
            using var http = new HttpClient(new AssignmentHandler(agent, device, target));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            UpdateHandoff? received = null;
            await RecoveryWorker.PollAsync(store, new CentralAssignmentClient(http, TimeProvider.System),
                new Uri("https://example.test/"), device, (handoff, _) =>
                {
                    received = handoff;
                    stop.Cancel();
                    return Task.CompletedTask;
                }, NullLogger.Instance, stop.Token);
            Assert.IsNotNull(received);
            Assert.AreEqual(target.UpdateId, received.UpdateId);
            Assert.AreEqual(credential, received.Credential);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task StagedRotationSurvivesOrdinarySyncAndUpdaterRestart()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"updater-rotation-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var agent = Guid.CreateVersion7();
            var current = $"vla_{agent:D}.{new string('a', 43)}";
            var pending = $"vla_{agent:D}.{new string('b', 43)}";
            var store = new MachineCredentialStore(directory);
            await store.WriteAsync(current, context.CancellationToken);
            await store.StageAsync(current, pending, context.CancellationToken);
            await store.WriteAsync(current, context.CancellationToken);
            var restarted = new MachineCredentialStore(directory);
            var values = await restarted.ReadCandidatesAsync(context.CancellationToken);
            Assert.IsNotNull(values);
            Assert.AreEqual(current, values.Current);
            Assert.AreEqual(pending, values.Pending);
            await restarted.WriteAsync(pending, context.CancellationToken);
            values = await store.ReadCandidatesAsync(context.CancellationToken);
            Assert.IsNotNull(values);
            Assert.AreEqual(pending, values.Current);
            Assert.IsNull(values.Pending);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task PollUsesPendingCredentialAfterCentralFinishesRotation()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"updater-pending-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var agent = Guid.CreateVersion7();
            var device = Guid.CreateVersion7();
            var current = $"vla_{agent:D}.{new string('a', 43)}";
            var pending = $"vla_{agent:D}.{new string('b', 43)}";
            var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), "1.0.0", "win-x64",
                new string('a', 64), Convert.ToBase64String(new byte[8]));
            var store = new MachineCredentialStore(directory);
            await store.StageAsync(current, pending, context.CancellationToken);
            using var http = new HttpClient(new RotatedAssignmentHandler(agent, device, target, current));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            string? selected = null;
            await RecoveryWorker.PollAsync(store, new CentralAssignmentClient(http, TimeProvider.System),
                new Uri("https://example.test/"), device, (handoff, _) =>
                {
                    selected = handoff.Credential;
                    stop.Cancel();
                    return Task.CompletedTask;
                }, NullLogger.Instance, stop.Token);
            Assert.AreEqual(pending, selected);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class RotatedAssignmentHandler(Guid agent, Guid device, AgentVersionTarget target, string rejected) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(request.Headers.Authorization?.Parameter == rejected ? new HttpResponseMessage(HttpStatusCode.Unauthorized) :
                request.RequestUri!.AbsolutePath.EndsWith("/identity", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = agent, DeviceId = device }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target) });
    }

    private sealed class AssignmentHandler(Guid agent, Guid device, AgentVersionTarget target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/identity", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = agent, DeviceId = device }) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target) });
    }
}
