using Temporalio.Client;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Infrastructure.Tests.Work;

[TestClass]
[TestCategory("Integration")]
public sealed class ServiceWorkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ProducerSubmitsWorkToTheDestinationWorkerAndReadsItsResult()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");

        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var id = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddVigiloomTemporalClient(address, "default");
        builder.Services.AddVigiloomTemporalWorker<TestHandler>(queue);
        using var host = builder.Build();
        await host.StartAsync(TestContext.CancellationToken);
        try
        {
            using var scope = host.Services.CreateScope();
            var producer = scope.ServiceProvider.GetRequiredService<ServiceWorkClient>();
            await producer.StartAsync(id, queue, new("inventory", "device-123"), TestContext.CancellationToken);
            var result = await producer.Get(id).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
            Assert.AreEqual("inventory:device-123", result);
        }
        finally { await host.StopAsync(CancellationToken.None); }
    }

    [TestMethod]
    [TestCategory("Database")]
    public async Task SqlOutboxDispatchesOnceAfterRollbackAndLostAcknowledgement()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");
        var client = await TemporalClient.ConnectAsync(new(address));
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var handler = new TestHandler();
        using var worker = ServiceWorkWorker.Create(client, queue, handler);
        await worker.ExecuteAsync(async () => await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
            var outboxHandler = new TemporalOutboxHandler(new ServiceWorkClient(client));
            var message = new OutboxMessage(Guid.CreateVersion7(), TemporalOutboxHandler.Type,
                JsonSerializer.Serialize(new TemporalWorkRequest(queue, new("inventory", "device-123"))));
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            var writer = new OutboxWriter([outboxHandler]);
            await using (var rollback = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await writer.WriteAsync(message, connection, rollback, token);
                await rollback.RollbackAsync(token);
            }
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await writer.WriteAsync(message, connection, transaction, token);
                await transaction.CommitAsync(token);
            }
            var dispatcher = new OutboxDispatcher(database.ConnectionString, [outboxHandler], NullLogger<OutboxDispatcher>.Instance);
            await dispatcher.DispatchAsync(token);
            var result = await new ServiceWorkClient(client).Get(message.MessageId.ToString("D")).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.AreEqual("inventory:device-123", result);
            Assert.AreEqual(message.MessageId.ToString("D"), handler.LastDeliveryId);
            await using (var simulateLostAck = connection.CreateCommand())
            {
                simulateLostAck.CommandText = "UPDATE scheduling.Outbox SET State = 0, AvailableAt = SYSDATETIMEOFFSET() WHERE MessageId = @id";
                simulateLostAck.Parameters.AddWithValue("@id", message.MessageId);
                await simulateLostAck.ExecuteNonQueryAsync(token);
            }
            await dispatcher.DispatchAsync(token);
            Assert.AreEqual(1, handler.Executions);
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT State FROM scheduling.Outbox WHERE MessageId = @id";
            check.Parameters.AddWithValue("@id", message.MessageId);
            Assert.AreEqual(2, (int)(await check.ExecuteScalarAsync(token))!);
        }, TestContext.CancellationToken), TestContext.CancellationToken);
    }

    public sealed class TestHandler : IServiceWorkHandler
    {
        public int Executions { get; private set; }
        public string? LastDeliveryId { get; private set; }
        public Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
        {
            Executions++;
            LastDeliveryId = work.DeliveryId;
            return Task.FromResult<string?>($"{work.Kind}:{work.Payload}");
        }
    }
}
