using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class MicrosoftCatalogCacheTests(TestContext context)
{
    [TestMethod]
    public async Task Cache_SharesProductDataAndBoundsRequestsAcrossBatches()
    {
        var calls = 0;
        using var cache = new MicrosoftCatalogCache((product, kb, token) =>
        {
            calls++;
            return Task.FromResult(new CatalogRelationResult(CatalogRelationStatus.Ready, "db2ed84f-9b54-41f7-b056-eb5d42725946", ["5006699"]));
        }, TimeProvider.System);
        var kbs = Enumerable.Range(0, 10).Select(x => (5031300 + x).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var first = await cache.GetBatchAsync("Windows Server 2022", kbs, context.CancellationToken);
        Assert.IsFalse(first.Complete);
        Assert.HasCount(8, first.Evidence);
        Assert.AreEqual(8, calls);
        var next = await cache.GetBatchAsync("Windows Server 2022 (Server Core installation)", kbs, context.CancellationToken);
        Assert.IsTrue(next.Complete);
        Assert.HasCount(10, next.Evidence);
        Assert.AreEqual(10, calls);
        Assert.IsTrue(next.Evidence.All(x => x.Product == "Windows Server 2022 (Server Core installation)"));
    }

    [TestMethod]
    public async Task Cache_FailuresDoNotBecomeEmptySuccessAndAreNotImmediatelyRetried()
    {
        var calls = 0;
        using var cache = new MicrosoftCatalogCache((product, kb, token) =>
        {
            calls++;
            return Task.FromResult(new CatalogRelationResult(CatalogRelationStatus.Unavailable));
        }, TimeProvider.System);
        Assert.IsFalse((await cache.GetBatchAsync("Windows Server 2022", ["5031364"], context.CancellationToken)).Complete);
        Assert.IsFalse((await cache.GetBatchAsync("Windows Server 2022", ["5031364"], context.CancellationToken)).Complete);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Cache_LimitsTotalRelationsAndReportsIncomplete()
    {
        var relations = Enumerable.Repeat("5006699", 5_001).ToArray();
        using var cache = new MicrosoftCatalogCache((product, kb, token) => Task.FromResult(
            new CatalogRelationResult(CatalogRelationStatus.Ready, "db2ed84f-9b54-41f7-b056-eb5d42725946", relations)), TimeProvider.System);
        var kbs = Enumerable.Range(0, 11).Select(x => (5031300 + x).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        _ = await cache.GetBatchAsync("Windows Server 2022", kbs, context.CancellationToken);
        var result = await cache.GetBatchAsync("Windows Server 2022", kbs, context.CancellationToken);
        Assert.IsFalse(result.Complete);
        Assert.HasCount(9, result.Evidence);
    }

    [TestMethod]
    public async Task Cache_HitDoesNotWaitForAnotherHostsSlowFetch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new MicrosoftCatalogCache(async (product, kb, token) =>
        {
            if (kb == "5031365") { started.SetResult(); await release.Task.WaitAsync(token); }
            return new CatalogRelationResult(CatalogRelationStatus.Ready, "db2ed84f-9b54-41f7-b056-eb5d42725946", ["5006699"]);
        }, TimeProvider.System);
        _ = await cache.GetBatchAsync("Windows Server 2022", ["5031364"], context.CancellationToken);
        var slow = cache.GetBatchAsync("Windows Server 2022", ["5031365"], context.CancellationToken);
        try
        {
            await started.Task.WaitAsync(context.CancellationToken);
            var hit = cache.GetBatchAsync("Windows Server 2022", ["5031364"], context.CancellationToken);
            Assert.IsTrue((await hit.WaitAsync(TimeSpan.FromSeconds(2), context.CancellationToken)).Complete);
        }
        finally { release.TrySetResult(); await slow; }
    }

    [TestMethod]
    public async Task Cache_TimedOutFetchDoesNotStarveLaterKbs()
    {
        var calls = new List<string>();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var cache = new MicrosoftCatalogCache(async (product, kb, token) =>
        {
            calls.Add(kb);
            if (kb == "5031364") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new CatalogRelationResult(CatalogRelationStatus.Ready, "db2ed84f-9b54-41f7-b056-eb5d42725946", ["5006699"]);
        }, clock, TimeSpan.FromMilliseconds(100));
        var kbs = new[] { "5031364", "5031365" };
        Assert.IsFalse((await cache.GetBatchAsync("Windows Server 2022", kbs, context.CancellationToken)).Complete);
        var duringBackoff = await cache.GetBatchAsync("Windows Server 2022", ["5031366"], context.CancellationToken);
        Assert.IsFalse(duringBackoff.Complete);
        Assert.HasCount(1, calls);
        clock.Now = clock.Now.AddMinutes(16);
        var retry = await cache.GetBatchAsync("Windows Server 2022", kbs, context.CancellationToken);
        Assert.IsFalse(retry.Complete);
        Assert.HasCount(1, retry.Evidence);
        CollectionAssert.AreEqual(kbs, calls);
    }

    [TestMethod]
    public async Task Cache_RunBudgetLimitsMissesAcrossHostsButStillServesHits()
    {
        var calls = 0;
        using var cache = new MicrosoftCatalogCache((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new CatalogRelationResult(CatalogRelationStatus.Ready, "db2ed84f-9b54-41f7-b056-eb5d42725946", ["5006699"]));
        }, TimeProvider.System);
        var budget = new CatalogRunBudget();
        var kbs = Enumerable.Range(0, 8).Select(x => (5031300 + x).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.IsTrue((await cache.GetBatchAsync("Windows Server 2022", kbs, context.CancellationToken, budget)).Complete);
        Assert.IsTrue(budget.Exhausted);
        Assert.IsFalse((await cache.GetBatchAsync("Windows Server 2022", ["5031366"], context.CancellationToken, budget)).Complete);
        Assert.IsTrue((await cache.GetBatchAsync("Windows Server 2022", [kbs[0]], context.CancellationToken, budget)).Complete);
        Assert.AreEqual(8, calls);
    }

    [TestMethod]
    public async Task Cache_UnsupportedProductDoesNotConsumeOtherHostsBudget()
    {
        using var cache = new MicrosoftCatalogCache((_, _, _) => throw new InvalidOperationException("Unexpected lookup."), TimeProvider.System);
        var budget = new CatalogRunBudget();
        var result = await cache.GetBatchAsync("Windows Server 2022, 23H2 Edition", ["5031364"], context.CancellationToken, budget);
        Assert.IsFalse(result.Complete);
        Assert.IsEmpty(result.Evidence);
        Assert.IsFalse(budget.Exhausted);
    }

    [TestMethod]
    [DataRow("Windows Server 2016")]
    [DataRow("Windows Server 2019")]
    [DataRow("Windows Server 2025")]
    public async Task Cache_CoreSharesOnlyItsOwnServerRelease(string product)
    {
        var calls = 0;
        using var cache = new MicrosoftCatalogCache((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new CatalogRelationResult(CatalogRelationStatus.Ready, "db2ed84f-9b54-41f7-b056-eb5d42725946", ["5006699"]));
        }, TimeProvider.System);
        Assert.IsTrue((await cache.GetBatchAsync(product, ["5031364"], context.CancellationToken)).Complete);
        var core = await cache.GetBatchAsync(product + " (Server Core installation)", ["5031364"], context.CancellationToken);
        Assert.IsTrue(core.Complete);
        Assert.AreEqual(product + " (Server Core installation)", core.Evidence.Single().Product);
        Assert.AreEqual(1, calls);
        _ = await cache.GetBatchAsync("Windows Server 2022", ["5031364"], context.CancellationToken);
        Assert.AreEqual(2, calls);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
