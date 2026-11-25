using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Agent = Vigiloom.Modules.AgentManagement.Domain.Agent;
using OldHostLog = Vigiloom.Modules.AgentManagement.Domain.HostLog;
using OldContext = Vigiloom.Modules.ServiceObservability.Domain.Telemetry.LogContext;
using OldRecord = Vigiloom.Modules.ServiceObservability.Domain.Telemetry.LogRecord;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class OperationsMigrationTests(TestContext context)
{
    [TestMethod]
    public Task CopiesExistingHostAndApplicationLogsWithTheirContext() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            var sourceServices = new ServiceCollection();
            sourceServices.AddModuleSqlServer<ResourcesDbContext>(source.ConnectionString, "resources");
            sourceServices.AddModuleSqlServer<AgentManagementDbContext>(source.ConnectionString, "agent");
            sourceServices.AddModuleSqlServer<ServiceObservabilityDbContext>(source.ConnectionString, "service");
            sourceServices.AddModuleSqlServer<OperationsDbContext>(source.ConnectionString, "operations");
            sourceServices.AddModuleSqlServer<LogAdmissionDbContext>(source.ConnectionString, "logquota");
            sourceServices.AddModuleSqlServer<SchedulingDbContext>(source.ConnectionString, "scheduling");
            using var sourceProvider = sourceServices.BuildServiceProvider();
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>(), token);
            var targetServices = new ServiceCollection();
            targetServices.AddModuleSqlServer<OperationsDbContext>(target.ConnectionString, "operations");
            targetServices.AddModuleSqlServer<LogAdmissionDbContext>(target.ConnectionString, "logquota");
            targetServices.AddModuleSqlServer<CentralLogsDbContext>(target.ConnectionString, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
            targetServices.AddModuleSqlServer<SchedulingDbContext>(target.ConnectionString, "scheduling");
            using var targetProvider = targetServices.BuildServiceProvider();
            await MigrateAsync(targetProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>(), token);
            await MigrateAsync(targetProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>(), token);
            await MigrateAsync(targetProvider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>(), token);
            await MigrateAsync(targetProvider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>(), token);

            var now = DateTimeOffset.UtcNow;
            var sourceQuota = new PolicyAdministration(sourceProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>());
            Assert.IsNotNull(await sourceQuota.UpdateAsync(new LogQuotaValues(7, 14, 120, 240), "", Guid.CreateVersion7(),
                (_, _, _, _) => Task.CompletedTask, token));
            var notification = new Notification { MessageId = Guid.CreateVersion7(), Source = "test", Title = "Alert", Detail = "details", OccurredAt = now };
            var protection = new EphemeralDataProtectionProvider();
            var group = new LineGroup { ExternalId = "group", Approved = true, LastSeenAt = now };
            var delivery = new LineDelivery { Recipient = group.ExternalId, Text = "Alert", GroupId = group.Id, CreatedAt = now };
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            {
                db.Set<Notification>().Add(notification); db.Set<LineDelivery>().Add(delivery); db.Set<LineGroup>().Add(group);
                db.Set<LineSettings>().Add(new LineSettings { Enabled = true,
                    ProtectedAccessToken = protection.CreateProtector("Vigiloom.Line.Channel.v1").Protect("token") });
                await db.SaveChangesAsync(token);
                // The monolith being migrated predates the aggregation columns.
                await db.Database.ExecuteSqlRawAsync("""
                    DECLARE @name sysname = (SELECT d.name FROM sys.default_constraints d
                        JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
                        WHERE d.parent_object_id = OBJECT_ID('operations.LineSettings') AND c.name = 'AggregationSeconds');
                    IF @name IS NOT NULL
                    BEGIN
                        DECLARE @sql nvarchar(max) = N'ALTER TABLE operations.LineSettings DROP CONSTRAINT ' + QUOTENAME(@name);
                        EXEC sp_executesql @sql;
                    END;
                    ALTER TABLE operations.LineSettings DROP COLUMN AggregationSeconds, AggregationStartedAt;
                    """, token);
            }
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            {
                var unrelated = Guid.CreateVersion7();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts) VALUES ({delivery.Id}, 'line.push.v1', '{{}}', {now}, {now}, 0, 0), ({unrelated}, 'other.work', '{{}}', {now}, {now}, 0, 0)", token);
            }
            var device = new Device("Server", "server.example", true, now);
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            var agent = new Agent(device.Id, 0, new byte[32], now);
            var record = new AgentLogRecord(Guid.CreateVersion7(), now, "host event", null, null, null, null, null);
            var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, Guid.CreateVersion7(),
                AgentLogSourceKind.TextFile, "system", 1, [record]);
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            {
                db.Agents.Add(agent); db.HostLogs.Add(new OldHostLog(agent.Id, batch, 0, now));
                db.Set<Vigiloom.Modules.AgentManagement.Domain.YaraScan>().AddRange(
                    new() { Id = Guid.CreateVersion7(), AgentId = agent.Id, DeviceId = device.Id, RequestedBy = Guid.CreateVersion7(),
                        FilePath = "C:/sample", Rules = "rule", MaximumBytes = 100, TimeoutSeconds = 10, RequestedAt = now, CompletedAt = now, Outcome = 1 },
                    new() { Id = Guid.CreateVersion7(), AgentId = agent.Id, DeviceId = device.Id, RequestedBy = Guid.CreateVersion7(),
                        FilePath = "C:/pending", Rules = "rule", MaximumBytes = 100, TimeoutSeconds = 10, RequestedAt = now });
                await db.SaveChangesAsync(token);
            }
            var telemetrySource = new ServiceSource("source", new byte[32], now);
            var application = new MonitoredService(new ServiceCandidate(telemetrySource.Id, "orders", "production", "Orders", now), now);
            var logContext = new OldContext { ServiceId = application.Id, ResourcePayload = [1], ScopePayload = [2], ResourceSchemaUrl = "resource", ScopeSchemaUrl = "scope" };
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            {
                db.Sources.Add(telemetrySource); db.Services.Add(application);
                db.LogRecords.Add(new OldRecord { ServiceId = application.Id, Context = logContext, OccurredAt = now, ReceivedAt = now, RecordPayload = [3] });
                await db.SaveChangesAsync(token);
            }
            await OperationsDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token);
            await using var logs = await targetProvider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token);
            Assert.IsTrue(await logs.HostLogs.AnyAsync(x => x.AgentId == agent.Id && x.MessageId == record.MessageId && x.Content == "host event", token));
            var copied = await logs.LogRecords.Include(x => x.Context).SingleAsync(token);
            Assert.AreEqual(application.Id, copied.ServiceId);
            CollectionAssert.AreEqual(new byte[] { 3 }, copied.RecordPayload);
            CollectionAssert.AreEqual(new byte[] { 1 }, copied.Context.ResourcePayload);
            var targetQuota = new PolicyAdministration(targetProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>());
            Assert.AreEqual(120, (await targetQuota.GetAsync(token)).Values.GlobalRate);
            await using (var db = await targetProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            {
                Assert.IsTrue(await db.Set<Notification>().AnyAsync(x => x.Id == notification.Id && x.Title == "Alert", token));
                Assert.IsTrue(await db.Set<LineDelivery>().AnyAsync(x => x.Id == delivery.Id && x.CompletedAt == null, token));
                Assert.AreEqual(10, (await db.Set<LineSettings>().SingleAsync(token)).AggregationSeconds);
                var timeline = await db.Set<TimelineRecord>().ToListAsync(token);
                Assert.HasCount(3, timeline);
                Assert.IsTrue(timeline.Any(x => x.Source == "host-log" && x.ResourceId == device.Id && x.LocalLogRowId != null && x.Evidence.Contains("host event")));
                Assert.IsTrue(timeline.Any(x => x.Source == "service-log" && x.ResourceId == application.Id && x.LocalLogRowId == copied.RowId));
                Assert.IsTrue(timeline.Any(x => x.Source == "yara" && x.ResourceId == device.Id && x.Summary == "YARA 掃描：C:/sample" && x.Evidence.EndsWith("結果 1")));
            }
            await using (var db = await targetProvider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.Outbox WHERE MessageId = {delivery.Id} AND MessageType = 'line.push.v1'").SingleAsync(token));
                Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
            }
            var push = new FakePush();
            using var http = new HttpClient(push);
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Operations"] = target.ConnectionString,
                    ["Temporal:Address"] = "127.0.0.1:1"
                }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.RemoveAll<IDataProtectionProvider>();
                    services.AddSingleton<IDataProtectionProvider>(protection);
                    services.RemoveAll<LinePushClient>();
                    services.AddSingleton(new LinePushClient(http));
                });
            });
            await using (var scope = host.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(token);
            Assert.AreEqual(1, push.Count);
            await using (var db = await targetProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
                Assert.IsNotNull((await db.Set<LineDelivery>().SingleAsync(x => x.Id == delivery.Id, token)).CompletedAt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => OperationsDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token));
        }, outerToken), context.CancellationToken);

    private static async Task MigrateAsync<T>(IDbContextFactory<T> factory, CancellationToken token) where T : DbContext
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.MigrateAsync(token);
    }

    private sealed class FakePush : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
