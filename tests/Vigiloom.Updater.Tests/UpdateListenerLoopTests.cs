using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Transport;
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
        }, _ => Task.FromResult(seen.Count < 2), NullLogger.Instance, context.CancellationToken);
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
        }, _ => Task.FromResult(true), NullLogger.Instance, stop.Token);
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
        var running = RecoveryWorker.RunListenerAsync(listener, (_, _) => throw new IOException("Interrupted update"),
            _ => { recovered.SetResult(); return Task.FromResult(false); }, NullLogger.Instance, context.CancellationToken);
        await listener.SendAsync(Request(), context.CancellationToken);
        await recovered.Task.WaitAsync(context.CancellationToken);
        await running.WaitAsync(context.CancellationToken);
        Assert.IsTrue(listener.Stopped);
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
