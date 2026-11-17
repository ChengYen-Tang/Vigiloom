using System.Threading.Channels;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Transport;
using Vigiloom.Updater.Core.Updates;
using Vigiloom.Updater.Hosting;

namespace Vigiloom.Updater.Tests;

[TestClass]
public sealed class UpdateListenerLoopTests(TestContext context)
{
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task ProcessesHandoffsInOrderWithoutWaitingForClientConnection()
    {
        await using var listener = new TestListener();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<Guid>();
        var first = Request();
        var second = Request();
        var running = RecoveryWorker.RunListenerAsync(listener, async (request, token) =>
        {
            seen.Add(request.UpdateId);
            if (request.UpdateId == first.UpdateId)
            {
                firstStarted.SetResult();
                await release.Task.WaitAsync(token);
            }
        }, (_, _) => Task.FromResult(seen.Count < 2), NullLogger.Instance, context.CancellationToken);
        await listener.SendAsync(first, context.CancellationToken);
        await firstStarted.Task.WaitAsync(context.CancellationToken);
        await listener.SendAsync(second, context.CancellationToken);
        Assert.HasCount(1, seen);
        release.SetResult();
        await running.WaitAsync(context.CancellationToken);
        CollectionAssert.AreEqual(new[] { first.UpdateId, second.UpdateId }, seen);
        Assert.IsTrue(listener.Stopped);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task StoppingHostCancelsProcessingAndClosesListener()
    {
        await using var listener = new TestListener();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = RecoveryWorker.RunListenerAsync(listener, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, (_, _) => Task.FromResult(true), NullLogger.Instance, stop.Token);
        await listener.SendAsync(Request(), context.CancellationToken);
        await started.Task.WaitAsync(context.CancellationToken);
        await stop.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => running);
        Assert.IsTrue(listener.Stopped);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task FailedRequestTriggersRecoveryWithoutAnotherHandoff()
    {
        await using var listener = new TestListener();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Request();
        var running = RecoveryWorker.RunListenerAsync(listener, (_, _) => throw new IOException("Interrupted update"),
            (current, _) => { Assert.AreSame(request, current); recovered.SetResult(); return Task.FromResult(false); }, NullLogger.Instance, context.CancellationToken);
        await listener.SendAsync(request, context.CancellationToken);
        await recovered.Task.WaitAsync(context.CancellationToken);
        await running.WaitAsync(context.CancellationToken);
        Assert.IsTrue(listener.Stopped);
    }

    [TestMethod]
    public async Task FailedRecoveryDeliversItsNewResultBeforeListenerStops()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"recovery-report-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var agent = Guid.CreateVersion7();
            var update = Guid.CreateVersion7();
            var request = new UpdateHandoff { UpdateId = update, Credential = $"vla_{agent:D}.{new string('a', 43)}" };
            var journal = new UpdateJournal(directory);
            var handler = new CaptureHandler();
            using var http = new HttpClient(handler);
            var delivery = new UpdateResultDelivery(http, journal, TimeProvider.System);
            await using var listener = new TestListener();
            var running = RecoveryWorker.RunListenerAsync(listener, (_, _) => Task.CompletedTask,
                (current, token) => RecoveryWorker.RecoverAndDeliverAsync(current, async ct =>
                {
                    var now = DateTimeOffset.UtcNow;
                    await journal.WriteAsync(new UpdateState(update, "old", "new", UpdatePhase.RollbackFailed, now,
                        [new(UpdatePhase.RollbackFailed, now, "Interrupted")], Error: "Interrupted",
                        Provenance: new(agent, Guid.CreateVersion7(), new string('a', 64))), ct);
                    return false;
                }, delivery, new Uri("https://example.test/"), NullLogger.Instance, token),
                NullLogger.Instance, context.CancellationToken);
            await listener.SendAsync(request, context.CancellationToken);
            await running.WaitAsync(context.CancellationToken);
            Assert.IsTrue(listener.Stopped);
            Assert.IsNotNull(handler.Report);
            Assert.AreEqual(AgentUpdateResultKind.RollbackFailed, handler.Report.Result);
            Assert.AreEqual(update, handler.Report.UpdateId);
            Assert.IsEmpty(await journal.ReadPendingAsync(agent, 10, context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public AgentUpdateResult? Report { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Report = await request.Content!.ReadFromJsonAsync<AgentUpdateResult>(cancellationToken);
            return new(HttpStatusCode.NoContent);
        }
    }

    private static UpdateHandoff Request() => new() { UpdateId = Guid.CreateVersion7(), Credential = "test" };

    private sealed class TestListener : ILocalUpdateListener
    {
        private readonly Channel<UpdateHandoff> requests = Channel.CreateUnbounded<UpdateHandoff>();
        public ChannelReader<UpdateHandoff> Requests => requests.Reader;
        public bool Stopped { get; private set; }
        public ValueTask SendAsync(UpdateHandoff request, CancellationToken token) => requests.Writer.WriteAsync(request, token);
        public async Task RunAsync(CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Stopped = true; requests.Writer.TryComplete(); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
