extern alias SecurityHost;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Temporalio.Client;
using Temporalio.Exceptions;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Operations.Work;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemporalAuditIntegrationTests(TestContext context)
{
    [TestMethod]
    public async Task SourceTransactionDeliversAuditToOperationsThroughTemporal()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run this integration test.");
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(target.ConnectionString, "operations");
            using var provider = services.BuildServiceProvider();
            await using (var db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
                .UseSqlServer(source.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options))
                await db.Database.MigrateAsync(token);
            var targetFactory = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await targetFactory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
                .UseSqlServer(target.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options))
                await db.Database.MigrateAsync(token);

            var queue = "vigiloom-operations-test-" + Guid.CreateVersion7().ToString("N");
            var resourceQueue = "vigiloom-audit-test-" + Guid.CreateVersion7().ToString("N");
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Operations"] = target.ConnectionString, ["Work:OperationsQueue"] = queue, ["Work:AuditQueue"] = resourceQueue });
            builder.Services.AddVigiloomTemporalClient(address, "default");
            builder.Services.AddScoped<NotificationWriter>();
            builder.Services.AddScoped<OperationAuditWriter>();
            builder.Services.AddScoped<TimelineWriter>();
            builder.Services.AddScoped<ResourceWork>();
            builder.Services.AddVigiloomTemporalWorker<OperationsWorkHandler>(queue);
            builder.Services.AddVigiloomTemporalWorker<OperationsWorkHandler>(resourceQueue);
            using var host = builder.Build();
            await host.StartAsync(token);
            var deliveryIds = new List<Guid>();
            try
            {
                using var scope = host.Services.CreateScope();
                var writer = scope.ServiceProvider.GetRequiredService<TemporalAuditWriter>();
                var audit = new OperationAudit(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow,
                    new GroupChanged(GroupAction.Created, 0, 0));
                await using var connection = new SqlConnection(source.ConnectionString);
                await connection.OpenAsync(token);
                await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
                {
                    await writer.AppendAsync(connection, transaction, audit, token);
                    await transaction.CommitAsync(token);
                }
                await using var select = connection.CreateCommand();
                select.CommandText = "SELECT MessageId FROM scheduling.Outbox";
                deliveryIds.Add((Guid)(await select.ExecuteScalarAsync(token))!);
                await using (var resources = new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                    .UseSqlServer(source.ConnectionString).Options))
                {
                    await using var transaction = await resources.Database.BeginTransactionAsync(token);
                    var resourceAudit = new GroupAuditRecorded(audit.ActorId, Guid.CreateVersion7(), audit.OccurredAt, GroupAction.Created, 0, 0);
                    deliveryIds.Add(await scope.ServiceProvider.GetRequiredService<ResourceWork>().PublishAsync(resources, resourceAudit, token));
                    await transaction.CommitAsync(token);
                }
                var device = Guid.CreateVersion7(); var service = Guid.CreateVersion7(); var targetId = Guid.CreateVersion7();
                var ping = new PingTransition(device, "router.example", 5, audit.OccurredAt, Reachability.Reachable, Reachability.Unreachable, new("Router", []));
                var health = new HealthTransition(targetId, service, null, 6, ServiceHealthState.Unhealthy, audit.OccurredAt, "Orders", "production");
                var securityNotification = new NotificationRequest(Guid.CreateVersion7(), "sigma", audit.OccurredAt, "Security alert", new string('測', 25000), device);
                await using (var resources = new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                    .UseSqlServer(source.ConnectionString).Options))
                {
                    await using var transaction = await resources.Database.BeginTransactionAsync(token);
                    var work = scope.ServiceProvider.GetRequiredService<TemporalWorkWriter>();
                    var sink = new TemporalPingTransitionSink(scope.ServiceProvider.GetRequiredService<OutboxWriter>(), host.Services.GetRequiredService<IConfiguration>());
                    await sink.RecordAsync(ping, resources.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
                    var writerType = typeof(SecurityHost::Program).Assembly.GetType("Vigiloom.Security.TemporalNotificationWriter", throwOnError: true)!;
                    var temporalHandler = scope.ServiceProvider.GetServices<IOutboxHandler>().Single(handler => handler.MessageType == TemporalOutboxHandler.Type);
                    var notificationWriter = (INotificationWriter)Activator.CreateInstance(writerType, new OutboxWriter([temporalHandler], maxPayloadLength: null), host.Services.GetRequiredService<IConfiguration>())!;
                    await notificationWriter.WriteAsync(securityNotification, resources.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
                    deliveryIds.Add(await work.WriteAsync(resources, queue, new(HealthTransitionDelivery.Kind, JsonSerializer.Serialize(new HealthTransitionDelivery(health, 0, []))), token));
                    await transaction.CommitAsync(token);
                }
                await using (var pending = connection.CreateCommand())
                {
                    pending.CommandText = "SELECT MessageId FROM scheduling.Outbox";
                    await using var rows = await pending.ExecuteReaderAsync(token);
                    while (await rows.ReadAsync(token))
                    {
                        var id = rows.GetGuid(0);
                        if (!deliveryIds.Contains(id)) deliveryIds.Add(id);
                    }
                }
                var outbox = new OutboxDispatcher(source.ConnectionString, scope.ServiceProvider.GetServices<IOutboxHandler>(),
                    NullLogger<OutboxDispatcher>.Instance);
                await outbox.DispatchAsync(token);
                foreach (var id in deliveryIds)
                    await scope.ServiceProvider.GetRequiredService<ServiceWorkClient>().Get(id.ToString("D"))
                        .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token);
                var query = new OperationAuditQuery(audit.OccurredAt.AddMinutes(-1), audit.OccurredAt.AddMinutes(1));
                var page = await new OperationAuditQueryService(targetFactory).QueryGroupsAsync(query, token);
                Assert.HasCount(2, page.Items);
                Assert.IsTrue(page.Items.All(item => item.ActorId == audit.ActorId));
                await using var operations = await targetFactory.CreateDbContextAsync(token);
                Assert.IsTrue(await operations.Set<PingAlert>().AnyAsync(item => item.DeviceId == device && item.RecoveredAt == null, token));
                Assert.IsTrue(await operations.Set<ServiceHealthAlert>().AnyAsync(item => item.ServiceId == service && item.RecoveredAt == null, token));
                Assert.AreEqual(3, await operations.Set<Notification>().CountAsync(token));
                Assert.IsTrue(await operations.Set<Notification>().AnyAsync(item => item.MessageId == securityNotification.MessageId, token));
            }
            finally
            {
                try { await host.StopAsync(CancellationToken.None); }
                finally
                {
                    if (deliveryIds.Count != 0)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var scope = host.Services.CreateScope();
                        foreach (var id in deliveryIds)
                        {
                            try
                            {
                                await scope.ServiceProvider.GetRequiredService<ITemporalClient>().WorkflowService.DeleteWorkflowExecutionAsync(new()
                                {
                                    Namespace = "default", WorkflowExecution = new() { WorkflowId = id.ToString("D") }
                                }, new RpcOptions { CancellationToken = cleanup.Token });
                            }
                            catch (RpcException error) when (error.Code == RpcException.StatusCode.NotFound) { }
                        }
                    }
                }
            }
        }, outerToken), context.CancellationToken);
    }
}
