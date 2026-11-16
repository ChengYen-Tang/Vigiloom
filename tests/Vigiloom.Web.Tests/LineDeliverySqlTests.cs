using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling.Outbox;
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
    public Task DeliveryRetryUsesSameLineRequestAfterTransactionRollback() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var channel = scope.ServiceProvider.GetRequiredService<LineChannel>();
        Assert.IsTrue(await channel.SaveAsync(new(false, "test-token", "test-secret"), token));
        await channel.RecordCandidatesAsync(["Cgroup"], token);
        var group = (await channel.GetAsync(token)).Groups.Single();
        Assert.IsTrue(await channel.SetGroupAsync(group.Id, "Ops", true, token));
        Assert.IsTrue(await channel.SaveAsync(new(true, null, null), token));
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
        Assert.IsTrue(await channel.SetGroupAsync(group.Id, "Ops", false, token));
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await handler.HandleAsync(new(cancelled.Value, LineDeliveryHandler.Type, "{}"), connection, transaction, token);
            await transaction.CommitAsync(token);
        }
        Assert.HasCount(2, transport.Requests);
        Assert.AreEqual("disabled", (await status.GetAsync(cancelled.Value, token))!.FailureCode);
    }, context.CancellationToken);

    private sealed class AcceptedThenConflict : HttpMessageHandler
    {
        public List<(string Key, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.GetValues("X-Line-Retry-Key").Single(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            var response = new HttpResponseMessage(Requests.Count == 1 ? HttpStatusCode.OK : HttpStatusCode.Conflict);
            if (Requests.Count > 1) response.Headers.Add("X-Line-Accepted-Request-Id", "accepted");
            return response;
        }
    }
}
