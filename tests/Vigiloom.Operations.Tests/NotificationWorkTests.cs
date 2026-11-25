using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Operations.Work;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NotificationWorkTests(TestContext context)
{
    [TestMethod]
    public Task ResourceServiceAuditWorkIsRecordedAndQueryable() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Operations"] = database.ConnectionString }).Build();
            var handler = new OperationsWorkHandler(config, new NotificationWriter(), new OperationAuditWriter());
            var actor = Guid.CreateVersion7();
            var device = Guid.CreateVersion7();
            var agent = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            object[] events =
            [
                new GroupAuditRecorded(actor, Guid.CreateVersion7(), now, GroupAction.Created, 0, 0),
                new PublicTemplateAuditRecorded(actor, Guid.CreateVersion7(), now, PublicationTemplateAction.Created, 0),
                new PublicationSettingAudit(actor, device, now, PublicationResourceKind.Device, false, true, true, false, false),
                new ApplicationApprovalAudit(actor, Guid.CreateVersion7(), Guid.CreateVersion7(), now),
                new ApplicationStateAudit(actor, Guid.CreateVersion7(), "stop", now),
                new AgentRegistrationAudit(device, agent, null, "AgentEnrolled", now),
                new AgentVersionAssignmentAudit(device, agent, actor, null, Guid.CreateVersion7(), now)
            ];
            foreach (var entry in events)
            {
                var delivery = new ServiceWork(entry.GetType().Name, JsonSerializer.Serialize(entry, entry.GetType()), Guid.CreateVersion7().ToString("D"));
                await handler.ExecuteAsync(delivery, token);
                await handler.ExecuteAsync(delivery, token);
            }
            var query = new OperationAuditQuery(now.AddMinutes(-1), now.AddMinutes(1));
            var reader = new OperationAuditQueryService(factory);
            Assert.HasCount(1, (await reader.QueryGroupsAsync(query, token)).Items);
            Assert.HasCount(2, (await reader.QueryPublicationsAsync(query, token)).Items);
            Assert.HasCount(1, (await reader.QueryServiceApprovalsAsync(query, token)).Items);
            Assert.HasCount(1, (await reader.QueryServiceCollectionsAsync(query, token)).Items);
            var registrations = (await reader.QueryAgentRegistrationAsync(query, token)).Items;
            Assert.HasCount(2, registrations);
            var enrolled = registrations.Single(item => item.AgentRegistration is not null);
            Assert.AreEqual(agent, enrolled.ActorId);
            Assert.IsTrue(enrolled.AgentRegistration!.AgentActor);
            var assignment = registrations.Single(item => item.AgentAssignment is not null);
            Assert.AreEqual(actor, assignment.ActorId);
            Assert.AreEqual(agent, assignment.AgentAssignment!.AgentId);
        }, context.CancellationToken);

    [TestMethod]
    public Task RetryDoesNotDuplicateNotificationAndChangedDeliveryIsRejected() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Operations"] = database.ConnectionString }).Build();
            var handler = new OperationsWorkHandler(config, new NotificationWriter(), new OperationAuditWriter());
            var notification = new NotificationRequest(Guid.CreateVersion7(), "ping", DateTimeOffset.UtcNow, "Ping unreachable", "Device: Router");
            var work = new ServiceWork(NotificationWork.Kind, JsonSerializer.Serialize(notification), Guid.CreateVersion7().ToString("D"));
            await handler.ExecuteAsync(work, token);
            await handler.ExecuteAsync(work, token);
            await handler.ExecuteAsync(work with { DeliveryId = Guid.CreateVersion7().ToString("D") }, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(work with { Payload = JsonSerializer.Serialize(notification with { Detail = "changed" }) }, token));
            await using var result = await factory.CreateDbContextAsync(token);
            var stored = await result.Set<Notification>().SingleAsync(token);
            Assert.AreEqual(notification.MessageId, stored.MessageId);
            Assert.AreEqual(notification.Detail, stored.Detail);

            var audit = new OperationAudit(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow,
                new GroupChanged(GroupAction.Created, 0, 0));
            var writer = new TemporalAuditWriter(new OutboxWriter([new TemporalType()]), config);
            await using (var source = await factory.CreateDbContextAsync(token))
            {
                await using var transaction = await source.Database.BeginTransactionAsync(token);
                Assert.Throws<ArgumentException>(() =>
                {
                    _ = writer.AppendAsync(source.Database.GetDbConnection(), transaction.GetDbTransaction(),
                        audit with { Details = new GroupChanged((GroupAction)999, 0, 0) }, token);
                });
                await writer.AppendAsync(source.Database.GetDbConnection(), transaction.GetDbTransaction(), audit, token);
                await transaction.RollbackAsync(token);
            }
            await using (var source = await factory.CreateDbContextAsync(token))
            {
                Assert.AreEqual(0, await source.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
                await using var transaction = await source.Database.BeginTransactionAsync(token);
                await writer.AppendAsync(source.Database.GetDbConnection(), transaction.GetDbTransaction(), audit, token);
                await transaction.CommitAsync(token);
            }
            await using (var source = await factory.CreateDbContextAsync(token))
            {
                var id = await source.Database.SqlQuery<Guid>($"SELECT MessageId AS [Value] FROM scheduling.Outbox").SingleAsync(token);
                var payload = await source.Database.SqlQuery<string>($"SELECT Payload AS [Value] FROM scheduling.Outbox").SingleAsync(token);
                var outgoing = JsonSerializer.Deserialize<TemporalWorkRequest>(payload);
                Assert.IsNotNull(outgoing);
                Assert.AreEqual(OperationsWork.Queue, outgoing.TaskQueue);
                var delivery = outgoing.Work with { DeliveryId = id.ToString("D") };
                await handler.ExecuteAsync(delivery, token);
                await handler.ExecuteAsync(delivery, token);
            }
            var query = new OperationAuditQuery(audit.OccurredAt.AddMinutes(-1), audit.OccurredAt.AddMinutes(1));
            var auditPage = await new OperationAuditQueryService(factory).QueryGroupsAsync(query, token);
            Assert.AreEqual(audit.ActorId, auditPage.Items.Single().ActorId);
        }, context.CancellationToken);

    private sealed class TemporalType : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
