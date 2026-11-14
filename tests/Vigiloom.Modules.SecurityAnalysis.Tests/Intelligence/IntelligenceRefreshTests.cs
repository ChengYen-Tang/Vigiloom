using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IntelligenceRefreshTests(TestContext context)
{
    [TestMethod]
    public Task ConcurrentInitialization_CreatesOneSource() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var refresh = new IntelligenceRefresh(contexts, new ClientFactory(() => throw new InvalidOperationException()), TimeProvider.System);
            var sources = await Task.WhenAll(refresh.EnsureSourceAsync(token), refresh.EnsureSourceAsync(token));
            Assert.AreEqual(sources[0].Id, sources[1].Id);
            await using var db = contexts.CreateDbContext();
            Assert.AreEqual(1, await db.IntelligenceSources.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task ConcurrentFirstRuns_CreateOneSourceAndOnlyOneDownload() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new Factory(options);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var calls = 0;
            var clients = new ClientFactory(() =>
            {
                Interlocked.Increment(ref calls);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"catalogVersion\":\"v1\",\"dateReleased\":\"2026-11-01T12:30:00Z\",\"count\":0,\"vulnerabilities\":[]}", Encoding.UTF8, "application/json")
                };
            });
            var refresh = new IntelligenceRefresh(contexts, clients, TimeProvider.System);
            var first = Task.Run(() => refresh.RunDueAsync(token), token);
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10), token));
                Assert.AreEqual(IntelligenceRefreshResult.Busy, await refresh.RunDueAsync(token));
            }
            finally
            {
                release.Set();
                try { await first; } catch { /* Preserve any assertion failure while ensuring the worker has stopped. */ }
            }
            Assert.AreEqual(IntelligenceRefreshResult.Staged, await first);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(IntelligenceRefreshResult.NotDue, await refresh.RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            Assert.AreEqual(1, await db.IntelligenceSources.CountAsync(token));
            var source = await db.IntelligenceSources.SingleAsync(token);
            source.Enabled = false;
            await db.SaveChangesAsync(token);
            Assert.AreEqual(IntelligenceRefreshResult.Disabled, await refresh.RunDueAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public void IsDue_UsesShortRetryAfterFailureAndDailyAfterSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new IntelligenceSource { Key = "cisa-kev", LastCheckedAt = now.AddHours(-1), LastError = "failed" };
        Assert.IsTrue(IntelligenceRefresh.IsDue(source, now));
        source.LastError = null;
        Assert.IsFalse(IntelligenceRefresh.IsDue(source, now));
        source.LastCheckedAt = now.AddDays(-2);
        Assert.IsTrue(IntelligenceRefresh.IsDue(source, now));
    }

    [TestMethod]
    public Task Lease_IsPerSourceAndCanBeReacquiredAfterRelease() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var firstSource = Guid.CreateVersion7();
            var otherSource = Guid.CreateVersion7();
            await using (var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, firstSource, token))
            {
                Assert.IsNotNull(lease);
                Assert.IsNull(await IntelligenceRefreshLease.TryAcquireAsync(contexts, firstSource, token));
                await using var other = await IntelligenceRefreshLease.TryAcquireAsync(contexts, otherSource, token);
                Assert.IsNotNull(other);
            }
            await using var again = await IntelligenceRefreshLease.TryAcquireAsync(contexts, firstSource, token);
            Assert.IsNotNull(again);
        }, context.CancellationToken);

    [TestMethod]
    public Task CancelledDownload_ReleasesLease() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                await db.SaveChangesAsync(token);
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var update = new KevUpdate(contexts, new WaitingClientFactory(waiting), TimeProvider.System);
            var operation = update.RunAsync(source.Id, cancellation.Token);
            try
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            }
            finally
            {
                cancellation.Cancel();
                try { await operation; } catch { /* Ensure cleanup before returning the database lease. */ }
            }
            await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, source.Id, token);
            Assert.IsNotNull(lease);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
    private sealed class ClientFactory(Func<HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response());
    }
    private sealed class WaitingClientFactory(TaskCompletionSource waiting) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new WaitingHandler(waiting));
    }
    private sealed class WaitingHandler(TaskCompletionSource waiting) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
