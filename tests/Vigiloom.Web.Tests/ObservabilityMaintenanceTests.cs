extern alias ObservabilityHost;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ObservabilityMaintenanceTests(TestContext context)
{
    [TestMethod]
    public Task RetentionUsesConfiguredDaysAndContinuesUntilOperationsFinishes() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var factory = new ContextFactory(options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync(token);
                db.TelemetryRetentionSettings.Add(new TelemetryRetentionSetting { LogDays = 45 });
                await db.SaveChangesAsync(token);
            }
            var handler = new CleanupHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://operations/") };
            var job = new ObservabilityRetentionJob(
                new TelemetryRetentionCleanup(factory), factory, new ClientFactory(client),
                NullLogger<ObservabilityRetentionJob>.Instance);
            await job.RunAsync(token);
            Assert.AreEqual(2, handler.Count);
            Assert.AreEqual(1, handler.HostCount);
        }, context.CancellationToken);

    private sealed class ContextFactory(DbContextOptions<ServiceObservabilityDbContext> options) : IDbContextFactory<ServiceObservabilityDbContext>
    {
        public ServiceObservabilityDbContext CreateDbContext() => new(options);
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) { Assert.IsTrue(name is "operations" or "legacy-web"); return client; }
    }
    private sealed class CleanupHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public int HostCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.IsTrue(path is "/internal/v1/application-logs/cleanup" or "/internal/v1/legacy-host-logs/cleanup" or "/internal/v1/timeline/health-cleanup" or "/internal/v1/timeline/trace-cleanup");
            var input = await request.Content!.ReadFromJsonAsync<ApplicationLogCleanupRequest>(cancellationToken: token);
            if (path.EndsWith("timeline/trace-cleanup", StringComparison.Ordinal))
            {
                Assert.AreEqual(7, input!.RetentionDays);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(0) };
            }
            if (path.EndsWith("timeline/health-cleanup", StringComparison.Ordinal))
            {
                Assert.AreEqual(365, input!.RetentionDays);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(0) };
            }
            Assert.AreEqual(45, input!.RetentionDays);
            if (path.EndsWith("legacy-host-logs/cleanup", StringComparison.Ordinal))
            {
                HostCount++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HostLogCleanupBatch(1, false)) };
            }
            Count++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ApplicationLogCleanupResult(Count == 1 ? 500 : 1, 0, Count == 1)) };
        }
    }
}
