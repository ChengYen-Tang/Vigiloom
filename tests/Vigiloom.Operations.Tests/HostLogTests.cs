using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OtlpLog = OpenTelemetry.Proto.Logs.V1.LogRecord;
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
using Vigiloom.Contracts.Events;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
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
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<CentralLogsDbContext>(database.ConnectionString, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = database.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
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
            using (var read = await client.PostAsJsonAsync("/internal/v1/host-logs/read", new HostLogReadRequest(input.AgentId, [item.MessageId]), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
                var records = await read.Content.ReadFromJsonAsync<HostLogEntry[]>(cancellationToken: token);
                Assert.HasCount(1, records!);
                Assert.AreEqual("login", records![0].Content);
                Assert.AreEqual(item.MessageId, records[0].MessageId);
            }
            using (var other = await client.PostAsJsonAsync("/internal/v1/host-logs/read", new HostLogReadRequest(Guid.CreateVersion7(), [item.MessageId]), token))
                Assert.IsEmpty((await other.Content.ReadFromJsonAsync<HostLogEntry[]>(cancellationToken: token))!);
            using (var invalid = await client.PostAsJsonAsync("/internal/v1/host-logs/read", new HostLogReadRequest(Guid.Empty, [item.MessageId]), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            await using var quota = await provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token);
            var remaining = await quota.Database.SqlQuery<long>($"SELECT Microtokens AS [Value] FROM logquota.Buckets WHERE Kind = 0").SingleAsync(token);
            using (var repeated = await client.PostAsJsonAsync("/internal/v1/host-logs", input, token)) Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
            Assert.AreEqual(remaining, await quota.Database.SqlQuery<long>($"SELECT Microtokens AS [Value] FROM logquota.Buckets WHERE Kind = 0").SingleAsync(token));
            using (var conflicting = await client.PostAsJsonAsync("/internal/v1/host-logs", input with { Batch = batch with { Items = [item with { Content = "changed" }] } }, token))
                Assert.AreEqual(HttpStatusCode.Conflict, conflicting.StatusCode);
            var payload = new OtlpLog { Body = new AnyValue { StringValue = "Application event" } }.ToByteArray();
            var appLog = new ApplicationLogSubmission(Guid.CreateVersion7(), Guid.CreateVersion7(), null,
                [1], [], "", "", [new ApplicationLogItem(DateTimeOffset.UtcNow, false, false, null, null, payload)]);
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
            await using var timeline = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await timeline.Set<TimelineRecord>().CountAsync(x => x.Source == "host-log" && x.ResourceId == input.DeviceId, token));
            Assert.Contains("login", (await timeline.Set<TimelineRecord>().SingleAsync(x => x.Source == "host-log", token)).Evidence);
            Assert.AreEqual(2, await timeline.Set<TimelineRecord>().CountAsync(x => x.Source == "service-log" && x.ResourceId == appLog.ServiceId, token));
            var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
            var until = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
            var events = await client.GetFromJsonAsync<EventTimelinePage>($"/internal/v1/events?from={from}&until={until}&source=service-log", token);
            Assert.HasCount(2, events!.Items);
            Assert.IsTrue(events.Items.All(entry => entry.Summary.Contains("Application event", StringComparison.Ordinal)));
            await logs.LogRecords.ExecuteDeleteAsync(token);
            await using (var scope = host.Services.CreateAsyncScope())
                Assert.AreEqual(2L, await scope.ServiceProvider.GetRequiredService<TimelineLogCleanup>().RunAsync(token));
            Assert.AreEqual(1, await timeline.Set<TimelineRecord>().CountAsync(token));
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
