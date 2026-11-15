using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class WindowsUpdateReaderTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void SupersededIds_NormalizeAndDeduplicateWithoutInventingRevisions()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        Assert.IsTrue(WindowsUpdateReader.TryNormalizeIds(["{AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA}"], out var result));
        CollectionAssert.AreEqual(new[] { "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" }, result.ToArray());
        Assert.IsFalse(WindowsUpdateReader.TryNormalizeIds(["not-a-guid"], out _));
        Assert.IsFalse(WindowsUpdateReader.TryNormalizeIds(["00000000-0000-0000-0000-000000000000"], out _));
        Assert.IsTrue(WindowsUpdateReader.TryNormalizeIds(["aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"], out var deduplicated));
        Assert.HasCount(1, deduplicated);
    }
    [TestMethod]
    public async Task BoundedRead_TimesOutWithoutReleasingGateUntilNativeOperationEnds()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        using var gate = new SemaphoreSlim(1, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<InventoryReadResult<WindowsUpdateObservation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<InventoryReadResult<WindowsUpdateObservation>> Pending(CancellationToken _) { entered.SetResult(); return finished.Task; }
        var first = WindowsUpdateReader.RunBoundedAsync(Pending, TimeSpan.FromMilliseconds(200), gate, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.AreEqual(InventoryReadStatus.Error, (await first).Status);
            Assert.AreEqual(0, gate.CurrentCount);
            var second = await WindowsUpdateReader.RunBoundedAsync(Pending, TimeSpan.FromMilliseconds(100), gate, CancellationToken.None);
            Assert.AreEqual(InventoryReadStatus.Error, second.Status);
            finished.TrySetResult(new(InventoryReadStatus.Partial, []));
            Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken));
            gate.Release();
        }
        finally
        {
            finished.TrySetResult(new(InventoryReadStatus.Partial, []));
            if (await gate.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)) gate.Release();
        }
    }

    [TestMethod]
    public async Task BoundedRead_PropagatesCallerCancellation()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<InventoryReadResult<WindowsUpdateObservation>> Pending(CancellationToken token)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(InventoryReadStatus.Partial, []);
        }
        var operation = WindowsUpdateReader.RunBoundedAsync(Pending, TimeSpan.FromSeconds(5), gate, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken));
            gate.Release();
        }
        finally
        {
            cancellation.Cancel();
            if (await gate.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)) gate.Release();
        }
    }
}
