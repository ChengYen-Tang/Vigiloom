using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostLogTests(TestContext context)
{
    [TestMethod]
    public Task HostAndApplicationLogsShareQuotaAndHostReplayIsDeduplicated() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<LogAdmissionDbContext>(database.ConnectionString, "logquota");
            services.AddModuleSqlServer<CentralLogsDbContext>(database.ConnectionString, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = database.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    var dispatcher = services.SingleOrDefault(item => item.ImplementationType?.Name == "WorkDispatcher");
                    if (dispatcher is not null) services.Remove(dispatcher);
                    services.RemoveAll<IOutboxHandler>();
                    services.AddScoped<IOutboxHandler, TestOutboxHandler>();
                });
            });
            using var client = host.CreateClient();
            var item = new AgentLogRecord(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "login", null, null, null, null, null);
            var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), DateTimeOffset.UtcNow,
                Guid.CreateVersion7(), AgentLogSourceKind.TextFile, "auth", 1, [item]);
            var input = new HostLogSubmission(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, batch);
            using (var first = await client.PostAsJsonAsync("/internal/v1/host-logs", input, token)) Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            await using var quota = await provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token);
            var remaining = await quota.Database.SqlQuery<long>($"SELECT Microtokens AS [Value] FROM logquota.Buckets WHERE Kind = 0").SingleAsync(token);
            using (var repeated = await client.PostAsJsonAsync("/internal/v1/host-logs", input, token)) Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
            Assert.AreEqual(remaining, await quota.Database.SqlQuery<long>($"SELECT Microtokens AS [Value] FROM logquota.Buckets WHERE Kind = 0").SingleAsync(token));
            using (var conflicting = await client.PostAsJsonAsync("/internal/v1/host-logs", input with { Batch = batch with { Items = [item with { Content = "changed" }] } }, token))
                Assert.AreEqual(HttpStatusCode.Conflict, conflicting.StatusCode);
            var appLog = new ApplicationLogSubmission(Guid.CreateVersion7(), Guid.CreateVersion7(), null,
                [1], [], "", "", [new ApplicationLogItem(DateTimeOffset.UtcNow, false, false, null, null, [2])]);
            using (var application = await client.PostAsJsonAsync("/internal/v1/application-logs", appLog, token))
                Assert.AreEqual(HttpStatusCode.OK, application.StatusCode);
            using (var duplicateApplication = await client.PostAsJsonAsync("/internal/v1/application-logs", appLog, token))
                Assert.AreEqual(HttpStatusCode.OK, duplicateApplication.StatusCode);
            Assert.AreEqual(1, await quota.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM logquota.Buckets WHERE Kind = 2 AND SourceId = {appLog.SourceId}").SingleAsync(token));
            await quota.Database.ExecuteSqlRawAsync("UPDATE logquota.Buckets SET Microtokens = 0, LastRefill = DATEADD(second, 30, SYSDATETIMEOFFSET()) WHERE Kind = 0", token);
            using (var limited = await client.PostAsJsonAsync("/internal/v1/application-logs", appLog, token))
                Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
            await using var logs = await provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await logs.HostLogs.CountAsync(token));
            Assert.AreEqual(2, await logs.LogRecords.CountAsync(token));
            await using var scheduling = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await scheduling.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
        }, context.CancellationToken);

    private sealed class TestOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
