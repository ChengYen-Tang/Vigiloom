using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryRefreshTests(TestContext context)
{
    [TestMethod]
    public Task RunDue_ResumesRateLimitedImportAndWaitsForActivation() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ => ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
            var refresh = new AdvisoryRefresh(contexts, clients, clock);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual(5, calls);
            Assert.AreEqual(AdvisoryRefreshResult.AwaitingActivation, await refresh.RunDueAsync(token));
            Assert.AreEqual(5, calls);
            await using (var check = contexts.CreateDbContext())
            {
                var import = await check.AdvisoryImports.SingleAsync(token);
                Assert.AreEqual(new DateOnly(1970, 1, 1), import.WindowStart);
                Assert.AreEqual(new DateOnly(2023, 2, 28), import.WindowEnd);
                Assert.IsNotNull(import.ResultRevisionId);
                Assert.AreEqual(0, await check.IntelligenceActivations.CountAsync(token));
            }
            clock.Advance(TimeSpan.FromDays(4));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual(9, calls);
            await using (var check = contexts.CreateDbContext()) Assert.AreEqual(2, await check.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_UsesOverlapFromActivatedRevision() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var clients = new ClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
            var refresh = new AdvisoryRefresh(contexts, clients, clock);
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                var first = await db.AdvisoryImports.SingleAsync(token);
                Assert.IsNotNull(first.ResultRevisionId);
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = first.SourceId, RevisionId = first.ResultRevisionId.Value, ActivatedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(token);
            }
            clock.Advance(TimeSpan.FromDays(2));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using var check = contexts.CreateDbContext();
            var latest = await check.AdvisoryImports.OrderByDescending(x => x.StartedAt).FirstAsync(token);
            Assert.AreEqual(new DateOnly(2023, 2, 21), latest.WindowStart);
            Assert.AreEqual(new DateOnly(2023, 3, 2), latest.WindowEnd);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_RefreshesAfterCandidatePolicyChanges() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") }; });
            var refresh = new AdvisoryRefresh(contexts, clients, clock);
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceSources.SingleAsync(token)).UpdateIntervalSeconds = 300;
                await db.SaveChangesAsync(token);
            }
            clock.Advance(TimeSpan.FromMinutes(11));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual(8, calls);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_PolicyChangesDuringRequestStillPreserveRateLimit() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ =>
            {
                calls++;
                if (calls == 1)
                {
                    using var db = contexts.CreateDbContext();
                    db.IntelligenceSources.Single().UpdateIntervalSeconds = 43_200;
                    db.SaveChanges();
                }
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return response;
            });
            var refresh = new AdvisoryRefresh(contexts, clients, clock);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(AdvisoryRefreshResult.Deferred, await refresh.RunDueAsync(token));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
            clock.Advance(TimeSpan.FromMinutes(61));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(2, calls);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_LateLimitOnAbandonedImportBlocksNewGeneration() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.TooManyRequests); });
            var refresh = new AdvisoryRefresh(contexts, clients, clock);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            var store = new AdvisoryImportStore(contexts, clock);
            AdvisoryImport old;
            await using (var db = contexts.CreateDbContext()) old = await db.AdvisoryImports.AsNoTracking().SingleAsync(token);
            Assert.IsTrue(await store.AbandonAsync(old.SourceId, old.Id, old.Version, token));
            clock.Advance(TimeSpan.FromMinutes(2));
            var next = await store.BeginAsync(old.SourceId, old.WindowStart, old.WindowEnd, token);
            Assert.IsNotNull(next);
            Assert.IsTrue(await store.DeferAsync(old.SourceId, old.Id, clock.GetUtcNow().AddHours(1), token));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_PolicyChangeAbandonsStaleImportBeforeRetry() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return response;
            });
            var refresh = new AdvisoryRefresh(contexts, clients, clock);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceSources.SingleAsync(token)).UpdateIntervalSeconds = 43_200;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(AdvisoryRefreshResult.Deferred, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext()) Assert.AreEqual(5, (await db.AdvisoryImports.SingleAsync(token)).Phase);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
            clock.Advance(TimeSpan.FromMinutes(61));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(2, calls);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
