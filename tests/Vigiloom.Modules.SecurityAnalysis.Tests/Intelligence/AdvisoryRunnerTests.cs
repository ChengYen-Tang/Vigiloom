using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryRunnerTests(TestContext context)
{
    [TestMethod]
    public Task SliceDeadline_CancelsBlockedDatabaseWriteWithoutAdvancingCursor() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var import = await new AdvisoryImportStore(contexts, clock).BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(import);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE [security].[IntelligenceSources] SET [Enabled] = [Enabled] WHERE [Id] = @id";
                command.Parameters.AddWithValue("@id", source.Id);
                await command.ExecuteNonQueryAsync(token);
            }
            try
            {
                var calls = 0;
                var clients = new ClientFactory(_ =>
                {
                    calls++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
                });
                var runner = new AdvisoryImportRunner(contexts, clients, clock);
                Assert.AreEqual(AdvisoryRunResult.Deferred, await runner.RunSliceAsync(source.Id, import.Id, null, token, TimeSpan.FromSeconds(3)));
                Assert.AreEqual(1, calls);
            }
            finally { await transaction.RollbackAsync(CancellationToken.None); }
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(0, (await check.AdvisoryImports.SingleAsync(token)).PageCount);
            Assert.AreEqual(0, await check.AdvisoryImportPages.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task RateLimit_SavesProgressAndResumesWithoutPublishing() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var store = new AdvisoryImportStore(contexts, clock);
            var import = await store.BeginAsync(source.Id, new(2023, 1, 1), new(2023, 2, 1), token);
            Assert.IsNotNull(import);
            var calls = 0;
            var clients = new ClientFactory(request =>
            {
                calls++;
                if (calls == 2)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                    return response;
                }
                if (calls == 3) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                if (calls == 1)
                {
                    var body = "[{\"ghsa_id\":\"GHSA-abcd-1234-efgh\",\"type\":\"reviewed\",\"published_at\":\"2023-01-01T00:00:00Z\",\"updated_at\":\"2023-01-03T00:00:00Z\",\"withdrawn_at\":null,\"vulnerabilities\":[]}]";
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/advisories?type=reviewed&per_page=100&sort=updated&direction=asc&modified=2023-01-01..2023-02-01&after=YQ%3D%3D>; rel=\"next\"");
                    return response;
                }
                if (calls == 4) Assert.Contains("after=YQ%3D%3D", request.RequestUri!.Query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            });
            var runner = new AdvisoryImportRunner(contexts, clients, clock);
            Assert.AreEqual(AdvisoryRunResult.RateLimited, await runner.RunSliceAsync(source.Id, import.Id, null, token));
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.AdvisoryImports.SingleAsync(token);
                Assert.AreEqual(1, saved.PageCount);
                Assert.AreEqual("YQ==", saved.Cursor);
                Assert.AreEqual(1, saved.RateLimitAttempts);
                Assert.IsNotNull(saved.NotBefore);
                Assert.IsGreaterThanOrEqualTo(clock.GetUtcNow().AddMinutes(2), saved.NotBefore.Value);
            }
            Assert.AreEqual(AdvisoryRunResult.RateLimited, await runner.RunSliceAsync(source.Id, import.Id, null, token));
            Assert.AreEqual(2, calls);
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.AreEqual(AdvisoryRunResult.RateLimited, await runner.RunSliceAsync(source.Id, import.Id, null, token));
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.AdvisoryImports.SingleAsync(token);
                Assert.AreEqual(2, saved.RateLimitAttempts);
                Assert.IsNotNull(saved.NotBefore);
                Assert.IsGreaterThanOrEqualTo(clock.GetUtcNow().AddMinutes(2), saved.NotBefore.Value);
            }
            Assert.AreEqual(AdvisoryRunResult.RateLimited, await runner.RunSliceAsync(source.Id, import.Id, null, token));
            Assert.AreEqual(3, calls);
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.AreEqual(AdvisoryRunResult.AwaitingReconciliation, await runner.RunSliceAsync(source.Id, import.Id, null, token));
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.AdvisoryImports.SingleAsync(token);
                Assert.AreEqual(3, saved.PageCount);
                Assert.AreEqual(3, saved.Phase);
                Assert.AreEqual(0, saved.RateLimitAttempts);
                Assert.IsNull(saved.NotBefore);
                Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
            }
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
