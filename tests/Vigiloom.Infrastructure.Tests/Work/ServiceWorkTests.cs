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

    [TestMethod]
    [TestCategory("Database")]
    public async Task PendingSqlWorkIsDeliveredAfterTemporalRecovers()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");
        var client = await TemporalClient.ConnectAsync(new(address));
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var id = Guid.CreateVersion7();
        var handler = new TestHandler();
        using var worker = ServiceWorkWorker.Create(client, queue, handler);
        try
        {
            await worker.ExecuteAsync(async () => await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
            {
                var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
                await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
                var offlineClient = TemporalClient.CreateLazy(new("127.0.0.1:1"));
                var offlineHandler = new TemporalOutboxHandler(new ServiceWorkClient(offlineClient));
                var message = new OutboxMessage(id, TemporalOutboxHandler.Type,
                    JsonSerializer.Serialize(new TemporalWorkRequest(queue, new("inventory", "device-123"))));
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
                {
                    await new OutboxWriter([offlineHandler]).WriteAsync(message, connection, transaction, token);
                    await transaction.CommitAsync(token);
                }
                await new OutboxDispatcher(database.ConnectionString, [offlineHandler], NullLogger<OutboxDispatcher>.Instance,
                    TimeSpan.FromSeconds(2)).DispatchAsync(token);
                await using (var check = connection.CreateCommand())
                {
                    check.CommandText = "SELECT State, Attempts, LastFailureCode FROM scheduling.Outbox WHERE MessageId = @id";
                    check.Parameters.AddWithValue("@id", id);
                    await using var reader = await check.ExecuteReaderAsync(token);
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(0, reader.GetInt32(0));
                    Assert.AreEqual(1, reader.GetInt32(1));
                    Assert.IsFalse(reader.IsDBNull(2));
                }
                await using (var retry = connection.CreateCommand())
                {
                    retry.CommandText = "UPDATE scheduling.Outbox SET AvailableAt = SYSDATETIMEOFFSET() WHERE MessageId = @id";
                    retry.Parameters.AddWithValue("@id", id);
                    await retry.ExecuteNonQueryAsync(token);
                }
                var onlineHandler = new TemporalOutboxHandler(new ServiceWorkClient(client));
                await new OutboxDispatcher(database.ConnectionString, [onlineHandler], NullLogger<OutboxDispatcher>.Instance).DispatchAsync(token);
                var result = await new ServiceWorkClient(client).Get(id.ToString("D")).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), token);
                Assert.AreEqual("inventory:device-123", result);
                Assert.AreEqual(1, handler.Executions);
                await using var completed = connection.CreateCommand();
                completed.CommandText = "SELECT State FROM scheduling.Outbox WHERE MessageId = @id";
                completed.Parameters.AddWithValue("@id", id);
                Assert.AreEqual(2, (int)(await completed.ExecuteScalarAsync(token))!);
            }, TestContext.CancellationToken), TestContext.CancellationToken);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await client.WorkflowService.DeleteWorkflowExecutionAsync(new()
                {
                    Namespace = "default",
                    WorkflowExecution = new() { WorkflowId = id.ToString("D") }
                }, new Temporalio.Client.RpcOptions { CancellationToken = cleanup.Token });
            }
            catch (Temporalio.Exceptions.RpcException error) when (error.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound) { }
        }
    }

    [TestMethod]
    public async Task MultipleWorkersShareQueueWithoutDuplicatingWork()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");
        var client = await TemporalClient.ConnectAsync(new(address));
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var ids = Enumerable.Range(0, 8).Select(_ => "vigiloom-test-" + Guid.CreateVersion7().ToString("N")).ToArray();
        var handler = new CountingHandler();
        var secondClient = await TemporalClient.ConnectAsync(new(address));
        using var first = ServiceWorkWorker.Create(client, queue, handler);
        using var second = ServiceWorkWorker.Create(secondClient, queue, handler);
        try
        {
            await first.ExecuteAsync(async () => await second.ExecuteAsync(async () =>
            {
                var producer = new ServiceWorkClient(client);
                var handles = await Task.WhenAll(ids.Select(id => producer.StartAsync(id, queue, new("test", id), TestContext.CancellationToken)));
                var results = await Task.WhenAll(handles.Select(handle => handle.GetResultAsync())).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
                CollectionAssert.AreEquivalent(ids, results);
                Assert.HasCount(ids.Length, handler.Counts);
                Assert.IsTrue(handler.Counts.Values.All(count => count == 1));
            }, TestContext.CancellationToken), TestContext.CancellationToken);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var id in ids)
            {
                try
                {
                    await client.WorkflowService.DeleteWorkflowExecutionAsync(new()
                    {
                        Namespace = "default",
                        WorkflowExecution = new() { WorkflowId = id }
                    }, new Temporalio.Client.RpcOptions { CancellationToken = cleanup.Token });
                }
                catch (Temporalio.Exceptions.RpcException error) when (error.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound) { }
            }
        }
    }

    private sealed class CountingHandler : IServiceWorkHandler
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Counts { get; } = new();
        public Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
        {
            Counts.AddOrUpdate(work.Payload, 1, (_, count) => count + 1);
            return Task.FromResult<string?>(work.Payload);
        }
    }

    [TestMethod]
    public async Task CancelRunningWorkReachesTheActivityHandler()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");
        var client = await TemporalClient.ConnectAsync(new(address));
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var id = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var handler = new CancelHandler();
        using var worker = ServiceWorkWorker.Create(client, queue, handler);
        try
        {
            await worker.ExecuteAsync(async () =>
            {
                try
                {
                    var workflow = await new ServiceWorkClient(client).StartAsync(id, queue, new("test", ""), TestContext.CancellationToken);
                    await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
                    await workflow.CancelAsync();
                    await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
                    var status = (await workflow.DescribeAsync()).Status;
                    Assert.AreEqual(Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running, status);
                    handler.AllowCompletion.TrySetResult();
                    await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(async () =>
                        await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken));
                    Assert.AreEqual(Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Canceled, (await workflow.DescribeAsync()).Status);
                }
                finally { handler.AllowCompletion.TrySetResult(); }
            }, TestContext.CancellationToken);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await client.WorkflowService.DeleteWorkflowExecutionAsync(new()
                {
                    Namespace = "default",
                    WorkflowExecution = new() { WorkflowId = id }
                }, new Temporalio.Client.RpcOptions { CancellationToken = cleanup.Token });
            }
            catch (Temporalio.Exceptions.RpcException error) when (error.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound) { }
        }
    }

    private sealed class CancelHandler : IServiceWorkHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                await AllowCompletion.Task;
                throw;
            }
            return null;
        }
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
