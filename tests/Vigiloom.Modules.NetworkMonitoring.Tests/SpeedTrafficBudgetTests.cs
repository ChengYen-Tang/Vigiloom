using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedTrafficBudgetTests
{
    [TestMethod]
    public async Task Content_SeparatesReceivedAndSentWhileSharingTheLimit()
    {
        var budget = new SpeedTrafficBudget(100, () => Assert.Fail("The budget should not be exhausted."), CancellationToken.None);
        using var sent = new SpeedBudgetContent(new ByteArrayContent(new byte[3]), budget, TrafficDirection.Sent);
        using var bufferedResponse = new SpeedBudgetContent(new ByteArrayContent(new byte[4]), budget, TrafficDirection.Received);
        using var streamedResponse = new SpeedBudgetContent(new ByteArrayContent(new byte[3]), budget, TrafficDirection.Received);
        await sent.CopyToAsync(Stream.Null, TestContext.CancellationToken);
        await bufferedResponse.CopyToAsync(Stream.Null, TestContext.CancellationToken);
        await using var responseStream = await streamedResponse.ReadAsStreamAsync(TestContext.CancellationToken);
        var buffer = new byte[3];
        Assert.AreEqual(3, await responseStream.ReadAsync(buffer, TestContext.CancellationToken));
        Assert.AreEqual(3L, budget.BytesSent);
        Assert.AreEqual(7L, budget.BytesReceived);
        Assert.AreEqual(10L, budget.BytesTransferred);
    }
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task OutstandingReservations_DoNotPrematurelyExhaustBudget()
    {
        using var stop = new CancellationTokenSource();
        var budget = new SpeedTrafficBudget(10, stop.Cancel, stop.Token);
        var first = await budget.ReserveAsync(10, stop.Token);
        var waiting = budget.ReserveAsync(1, stop.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        Assert.IsFalse(budget.Reached);

        budget.Complete(first, 4);
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        Assert.AreEqual(1, second);
        budget.Complete(second, 1);
        Assert.AreEqual(5L, budget.BytesTransferred);
        Assert.IsFalse(budget.Reached);
        Assert.IsFalse(stop.IsCancellationRequested);
    }

    [TestMethod]
    public async Task WaitingReservation_StopsWhenOperationIsCancelled()
    {
        using var stop = new CancellationTokenSource();
        var budget = new SpeedTrafficBudget(10, stop.Cancel, stop.Token);
        var first = await budget.ReserveAsync(10, stop.Token);
        var waiting = budget.ReserveAsync(1, stop.Token).AsTask();
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken));
        budget.Complete(first, 0);
        Assert.AreEqual(0L, budget.BytesTransferred);
        Assert.IsFalse(budget.Reached);
    }

    [TestMethod]
    public async Task CancelledCaller_DoesNotPoisonLaterReservations()
    {
        using var stop = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var budget = new SpeedTrafficBudget(10, stop.Cancel, stop.Token);
        var first = await budget.ReserveAsync(10, stop.Token);
        var waiting = budget.ReserveAsync(1, caller.Token).AsTask();
        await caller.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken));
        budget.Complete(first, 4);

        var next = await budget.ReserveAsync(6, stop.Token);
        Assert.AreEqual(6, next);
        budget.Complete(next, 6);
        Assert.AreEqual(10L, budget.BytesTransferred);
        Assert.IsTrue(budget.Reached);
    }

    [TestMethod]
    public async Task RefundedReservation_WakesMultipleConcurrentWaiters()
    {
        using var stop = new CancellationTokenSource();
        var budget = new SpeedTrafficBudget(10, stop.Cancel, stop.Token);
        var first = await budget.ReserveAsync(10, stop.Token);
        var left = budget.ReserveAsync(4, stop.Token).AsTask();
        var right = budget.ReserveAsync(4, stop.Token).AsTask();
        Assert.IsFalse(left.IsCompleted);
        Assert.IsFalse(right.IsCompleted);

        budget.Complete(first, 4);
        var allocations = await Task.WhenAll(left, right).WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        Assert.AreEqual(6, allocations.Sum());
        foreach (var allocation in allocations) budget.Complete(allocation, allocation);
        Assert.AreEqual(10L, budget.BytesTransferred);
        Assert.IsTrue(budget.Reached);
    }

    [TestMethod]
    public async Task PartialWrite_WaitsForUnusedReservationInsteadOfAborting()
    {
        using var stop = new CancellationTokenSource();
        var budget = new SpeedTrafficBudget(10, stop.Cancel, stop.Token);
        var held = await budget.ReserveAsync(9, stop.Token);
        using var destination = new MemoryStream();
        using var stream = new SpeedBudgetStream(destination, budget, TrafficDirection.Sent, leaveOpen: true);

        var writing = stream.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, stop.Token).AsTask();
        Assert.IsFalse(writing.IsCompleted, "The remaining bytes should wait for the outstanding reservation.");
        Assert.AreEqual(1L, destination.Length);
        budget.Complete(held, 0);
        await writing.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, destination.ToArray());
        Assert.AreEqual(5L, budget.BytesTransferred);
        Assert.IsFalse(budget.Reached);
    }

    [TestMethod]
    public async Task FailedWrite_ReturnsReservationAndWakesWaiter()
    {
        using var stop = new CancellationTokenSource();
        var budget = new SpeedTrafficBudget(10, stop.Cancel, stop.Token);
        using var destination = new FailingWriteStream();
        using var stream = new SpeedBudgetStream(destination, budget, TrafficDirection.Sent, leaveOpen: true);
        var writing = stream.WriteAsync(new byte[10], stop.Token).AsTask();
        await destination.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        var waiting = budget.ReserveAsync(3, stop.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted);

        destination.Release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken));
        var reservation = await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        Assert.AreEqual(3, reservation);
        budget.Complete(reservation, 0);
        Assert.AreEqual(0L, budget.BytesTransferred);
        Assert.IsFalse(budget.Reached);
    }

    private sealed class FailingWriteStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw new IOException("Simulated transport failure");
        }
    }

    [TestMethod]
    public async Task ActualTransferredBytes_ExhaustBudgetAndStopOtherWaiters()
    {
        using var stop = new CancellationTokenSource();
        var budget = new SpeedTrafficBudget(10, stop.Cancel, stop.Token);
        var first = await budget.ReserveAsync(10, stop.Token);
        var waiting = budget.ReserveAsync(1, stop.Token).AsTask();
        budget.Complete(first, 10);
        var error = await Assert.ThrowsAsync<Exception>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken));
        Assert.IsTrue(error is OperationCanceledException or IOException);
        Assert.AreEqual(10L, budget.BytesTransferred);
        Assert.IsTrue(budget.Reached);
        Assert.IsTrue(stop.IsCancellationRequested);
    }
}
