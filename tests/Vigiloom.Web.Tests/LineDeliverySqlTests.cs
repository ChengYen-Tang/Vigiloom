using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LineDeliverySqlTests(TestContext context)
{
    [TestMethod]
    public Task NotificationsAreGroupedAfterConfiguredWindow() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var channel = scope.ServiceProvider.GetRequiredService<LineChannel>();
        var actor = Guid.CreateVersion7();
        Assert.IsTrue(await channel.SaveAsync(new(false, "test-token", "test-secret", 10), actor, token));
        await channel.RecordCandidatesAsync(["Cgroup"], token);
        var group = (await channel.GetAsync(token)).Groups.Single();
        Assert.IsTrue(await channel.SetGroupAsync(group.Id, "Ops", true, actor, token));
        Assert.IsTrue(await channel.SaveAsync(new(true, null, null), actor, token));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            db.Set<Notification>().AddRange(
                new Notification { MessageId = Guid.CreateVersion7(), Source = "ping", OccurredAt = DateTimeOffset.UtcNow, Title = "First", Detail = "Host A" },
                new Notification { MessageId = Guid.CreateVersion7(), Source = "ping", OccurredAt = DateTimeOffset.UtcNow, Title = "Second", Detail = "Host B" });
            for (var i = 0; i < 99; i++)
                db.Set<Notification>().Add(new Notification { MessageId = Guid.CreateVersion7(), Source = "ping",
                    OccurredAt = DateTimeOffset.UtcNow, Title = $"Additional {i}", Detail = "Unavailable" });
            await db.SaveChangesAsync(token);
        }
        var clock = new MutableClock();
        var queue = new LineDeliveryQueue(contexts, scope.ServiceProvider.GetRequiredService<OutboxWriter>(), clock);
        await queue.EnqueueNewAsync(token);
        clock.Now = clock.Now.AddSeconds(9);
        await queue.EnqueueNewAsync(token);
        await using (var db = await contexts.CreateDbContextAsync(token))
            Assert.AreEqual(0, await db.Set<LineDelivery>().CountAsync(token));
        clock.Now = clock.Now.AddSeconds(1);
        await new LineDeliveryQueue(contexts, scope.ServiceProvider.GetRequiredService<OutboxWriter>(), clock).EnqueueNewAsync(token);
        await queue.EnqueueNewAsync(token);
        await using var result = await contexts.CreateDbContextAsync(token);
        var deliveries = await result.Set<LineDelivery>().OrderBy(item => item.RowId).ToArrayAsync(token);
        Assert.HasCount(2, deliveries);
        var entries = deliveries.SelectMany(item => item.Text.Split("\n\n")).ToArray();
        Assert.HasCount(101, entries);
        Assert.Contains("First\nHost A", entries);
        Assert.Contains("Second\nHost B", entries);
        for (var i = 0; i < 99; i++) Assert.Contains($"Additional {i}\nUnavailable", entries);
    }, context.CancellationToken);

    [TestMethod]
    public Task DeliveryRetryUsesSameLineRequestAfterTransactionRollback() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var channel = scope.ServiceProvider.GetRequiredService<LineChannel>();
        var actor = Guid.CreateVersion7();
        Assert.IsTrue(await channel.SaveAsync(new(false, "test-token", "test-secret", 0), actor, token));
        await channel.RecordCandidatesAsync(["Cgroup"], token);
        var group = (await channel.GetAsync(token)).Groups.Single();
        Assert.IsTrue(await channel.SetGroupAsync(group.Id, "Ops", true, actor, token));
        Assert.IsTrue(await channel.SaveAsync(new(true, null, null), actor, token));
        var audit = scope.ServiceProvider.GetRequiredService<OperationAuditQueryService>();
        var auditPage = await audit.QueryLineAsync(new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), 1), token);
        Assert.HasCount(3, auditPage.Items);
        Assert.IsTrue(auditPage.Items.All(entry => entry.ActorId == actor && entry.LineSettings is not null));
        Assert.DoesNotContain("test-token", System.Text.Json.JsonSerializer.Serialize(auditPage));
        Assert.DoesNotContain("test-secret", System.Text.Json.JsonSerializer.Serialize(auditPage));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            db.Set<Notification>().Add(new Notification { MessageId = Guid.CreateVersion7(), Source = "ping",
                OccurredAt = DateTimeOffset.UtcNow, Title = "Ping alert", Detail = "Host unavailable" });
            await db.SaveChangesAsync(token);
        }
        await scope.ServiceProvider.GetRequiredService<LineDeliveryQueue>().EnqueueNewAsync(token);
        Guid id;
        await using (var db = await contexts.CreateDbContextAsync(token)) id = (await db.Set<LineDelivery>().SingleAsync(token)).Id;
        var status = scope.ServiceProvider.GetRequiredService<LineDeliveryStatus>();
        Assert.AreEqual("pending", (await status.GetAsync(id, token))!.Status);
        var transport = new AcceptedThenConflict();
        using var client = new HttpClient(transport);
        var handler = new LineDeliveryHandler(contexts, new LinePushClient(client),
            factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await handler.HandleAsync(new(id, LineDeliveryHandler.Type, "{}"), connection, transaction, token);
            await transaction.RollbackAsync(token);
        }
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await handler.HandleAsync(new(id, LineDeliveryHandler.Type, "{}"), connection, transaction, token);
            await transaction.CommitAsync(token);
        }
        Assert.HasCount(2, transport.Requests);
        Assert.AreEqual(transport.Requests[0], transport.Requests[1]);
        Assert.AreEqual(id.ToString("D"), transport.Requests[0].Key);
        await using var result = await contexts.CreateDbContextAsync(token);
        var delivery = await result.Set<LineDelivery>().SingleAsync(token);
        Assert.IsNotNull(delivery.FirstAttemptAt);
        Assert.IsNotNull(delivery.CompletedAt);
        Assert.IsNull(delivery.FailureCode);
        Assert.AreEqual("accepted", (await status.GetAsync(id, token))!.Status);

        var cancelled = await scope.ServiceProvider.GetRequiredService<LineDeliveryQueue>().EnqueueTestAsync(group.Id, token);
        Assert.IsNotNull(cancelled);
        Assert.IsTrue(await channel.SetGroupAsync(group.Id, "Ops", false, actor, token));
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await handler.HandleAsync(new(cancelled.Value, LineDeliveryHandler.Type, "{}"), connection, transaction, token);
            await transaction.CommitAsync(token);
        }
        Assert.HasCount(2, transport.Requests);
        Assert.AreEqual("disabled", (await status.GetAsync(cancelled.Value, token))!.FailureCode);

        Assert.IsTrue(await channel.SetGroupAsync(group.Id, "Ops", true, actor, token));
        var testId = await scope.ServiceProvider.GetRequiredService<LineDeliveryQueue>().EnqueueTestAsync(group.Id, token);
        Assert.IsNotNull(testId);
        var dispatcher = new OutboxDispatcher(database.ConnectionString, [handler], NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.DispatchAsync(token);
        Assert.AreEqual("accepted", (await status.GetAsync(testId.Value, token))!.Status);
        Assert.HasCount(3, transport.Requests);
        var recent = await status.GetRecentAsync(token);
        Assert.AreEqual("accepted", recent.Single(x => x.Id == testId.Value).Status);
        Assert.AreEqual("failed", recent.Single(x => x.Id == cancelled.Value).Status);
        Assert.AreEqual("Ops", recent.Single(x => x.Id == cancelled.Value).GroupName);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM scheduling.Outbox WHERE MessageId = @id";
        command.Parameters.AddWithValue("@id", testId.Value);
        Assert.AreEqual(2, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    }, context.CancellationToken);

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AcceptedThenConflict : HttpMessageHandler
    {
        public List<(string Key, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.GetValues("X-Line-Retry-Key").Single();
            var repeated = Requests.Any(previous => previous.Key == key);
            Requests.Add((key, await request.Content!.ReadAsStringAsync(cancellationToken)));
            var response = new HttpResponseMessage(repeated ? HttpStatusCode.Conflict : HttpStatusCode.OK);
            if (repeated) response.Headers.Add("X-Line-Accepted-Request-Id", "accepted");
            return response;
        }
    }
}
