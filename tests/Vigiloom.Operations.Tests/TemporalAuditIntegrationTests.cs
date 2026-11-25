using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Client;
using Temporalio.Exceptions;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
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
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Operations"] = target.ConnectionString, ["Work:OperationsQueue"] = queue });
            builder.Services.AddVigiloomTemporalClient(address, "default");
            builder.Services.AddScoped<NotificationWriter>();
            builder.Services.AddScoped<OperationAuditWriter>();
            builder.Services.AddVigiloomTemporalWorker<OperationsWorkHandler>(queue);
            using var host = builder.Build();
            await host.StartAsync(token);
            var deliveryId = Guid.Empty;
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
                deliveryId = (Guid)(await select.ExecuteScalarAsync(token))!;
                var outbox = new OutboxDispatcher(source.ConnectionString, scope.ServiceProvider.GetServices<IOutboxHandler>(),
                    NullLogger<OutboxDispatcher>.Instance);
                await outbox.DispatchAsync(token);
                await scope.ServiceProvider.GetRequiredService<ServiceWorkClient>().Get(deliveryId.ToString("D"))
                    .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token);
                var query = new OperationAuditQuery(audit.OccurredAt.AddMinutes(-1), audit.OccurredAt.AddMinutes(1));
                var page = await new OperationAuditQueryService(targetFactory).QueryGroupsAsync(query, token);
                Assert.AreEqual(audit.ActorId, page.Items.Single().ActorId);
            }
            finally
            {
                try { await host.StopAsync(CancellationToken.None); }
                finally
                {
                    if (deliveryId != Guid.Empty)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var scope = host.Services.CreateScope();
                        try
                        {
                            await scope.ServiceProvider.GetRequiredService<ITemporalClient>().WorkflowService.DeleteWorkflowExecutionAsync(new()
                            {
                                Namespace = "default", WorkflowExecution = new() { WorkflowId = deliveryId.ToString("D") }
                            }, new RpcOptions { CancellationToken = cleanup.Token });
                        }
                        catch (RpcException error) when (error.Code == RpcException.StatusCode.NotFound) { }
                    }
                }
            }
        }, outerToken), context.CancellationToken);
    }
}
