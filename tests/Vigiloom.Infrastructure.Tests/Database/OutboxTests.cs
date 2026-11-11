using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class OutboxTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task BusinessRollback_DoesNotPublish_AndDuplicateMessageIdIsRejected() => WithDatabase(async (connectionString, token) =>
    {
        var handler = new TestHandler();
        var writer = new OutboxWriter([handler]);
        var message = new OutboxMessage(Guid.CreateVersion7(), handler.MessageType, "{\"value\":1}");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        Assert.AreEqual(1, await CountAsync(connection, "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('scheduling.Outbox') AND is_primary_key=1 AND type_desc='CLUSTERED'", token));
        Assert.AreEqual(1, await CountAsync(connection, "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('scheduling.Outbox') AND name='IX_Outbox_MessageId' AND is_unique=1 AND type_desc='NONCLUSTERED'", token));
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await writer.WriteAsync(message, connection, transaction, token);
            await transaction.RollbackAsync(token);
        }
        Assert.AreEqual(0, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.Outbox", token));
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await writer.WriteAsync(message, connection, transaction, token);
            await transaction.CommitAsync(token);
        }
        await using (var duplicate = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await Assert.ThrowsAsync<SqlException>(() => writer.WriteAsync(message, connection, duplicate, token));
            await duplicate.RollbackAsync(token);
        }
        Assert.AreEqual(1, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.Outbox", token));
    });

    [TestMethod]
    public Task ConcurrentDispatchers_ApplyOneEffect_AndRecoverExpiredLease() => WithIsolatedDatabase(async (connectionString, token) =>
    {
        await using (var configuration = new SqlConnection(connectionString))
        {
            await configuration.OpenAsync(token);
            await using var enableSnapshot = configuration.CreateCommand();
            enableSnapshot.CommandText = "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE";
            await enableSnapshot.ExecuteNonQueryAsync(token);
        }
        var handler = new TestHandler();
        var message = await EnqueueAsync(connectionString, handler, token);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE scheduling.Outbox SET State=1, Attempts=1, LeaseId=@lease, LeaseUntil=DATEADD(second,-1,SYSUTCDATETIME())";
            command.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = Guid.CreateVersion7();
            await command.ExecuteNonQueryAsync(token);
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Dispatcher(connectionString, handler).DispatchAsync(token)));
        await using var verify = new SqlConnection(connectionString);
        await verify.OpenAsync(token);
        Assert.AreEqual(1, await CountAsync(verify, "SELECT COUNT(*) FROM scheduling.TestEffects", token));
        await using var result = verify.CreateCommand();
        result.CommandText = "SELECT State, MessageId FROM scheduling.Outbox";
        await using var reader = await result.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.AreEqual(message.MessageId, reader.GetGuid(1));
    });

    [TestMethod]
    public Task FailedHandler_RollsBackEffect_AndRetriesWithSameMessageId() => WithDatabase(async (connectionString, token) =>
    {
        var handler = new TestHandler { Fail = true };
        var message = await EnqueueAsync(connectionString, handler, token);
        await Dispatcher(connectionString, handler).DispatchAsync(token);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        Assert.AreEqual(0, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.TestEffects", token));
        Assert.AreEqual(1, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.Outbox WHERE State=0 AND Attempts=1 AND LastFailureCode='handler_failed' AND AvailableAt > CreatedAt", token));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE scheduling.Outbox SET AvailableAt=TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
            await command.ExecuteNonQueryAsync(token);
        }
        handler.Fail = false;
        await Dispatcher(connectionString, handler).DispatchAsync(token);
        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT MessageId FROM scheduling.TestEffects";
        Assert.AreEqual(message.MessageId, (Guid)(await verify.ExecuteScalarAsync(token))!);
    });

    [TestMethod]
    public Task ExpiredDuringHandling_RollsBackTheEffect() => WithDatabase(async (connectionString, token) =>
    {
        var handler = new TestHandler { ExpireLease = true };
        await EnqueueAsync(connectionString, handler, token);
        await Dispatcher(connectionString, handler).DispatchAsync(token);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        Assert.AreEqual(0, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.TestEffects", token));
        Assert.AreEqual(1, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.Outbox WHERE State=1", token));
    });

    [TestMethod]
    public Task RepeatedWorkerCrashes_StopAfterTheRetryLimit() => WithDatabase(async (connectionString, token) =>
    {
        var handler = new TestHandler();
        await EnqueueAsync(connectionString, handler, token);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE scheduling.Outbox SET State=1, Attempts=10, LeaseId=@lease, LeaseUntil=DATEADD(second,-1,SYSUTCDATETIME())";
            command.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = Guid.CreateVersion7();
            await command.ExecuteNonQueryAsync(token);
        }
        await Dispatcher(connectionString, handler).DispatchAsync(token);
        await using var verify = new SqlConnection(connectionString);
        await verify.OpenAsync(token);
        Assert.AreEqual(0, await CountAsync(verify, "SELECT COUNT(*) FROM scheduling.TestEffects", token));
        Assert.AreEqual(1, await CountAsync(verify, "SELECT COUNT(*) FROM scheduling.Outbox WHERE State=3 AND Attempts=10 AND LastFailureCode='attempts_exhausted'", token));
    });

    [TestMethod]
    public Task Cancellation_RollsBackTheEffect_AndAnExpiredLeaseCanBeRetried() => WithDatabase(async (connectionString, token) =>
    {
        var handler = new TestHandler { WaitForCancellation = true };
        var message = await EnqueueAsync(connectionString, handler, token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var dispatch = Dispatcher(connectionString, handler).DispatchAsync(cancel.Token);
        Exception? primaryFailure = null;
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await cancel.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => dispatch);
        }
        catch (Exception error) { primaryFailure = error; }
        List<Exception> cleanupFailures = [];
        try { await cancel.CancelAsync(); } catch (Exception error) { cleanupFailures.Add(error); }
        try { await dispatch; }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { cleanupFailures.Add(error); }
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count != 0)
                throw new AggregateException("Test and dispatch cleanup failed.", [primaryFailure, .. cleanupFailures]);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailures.Count != 0) throw new AggregateException("Dispatch cleanup failed.", cleanupFailures);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(token);
            Assert.AreEqual(0, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.TestEffects", token));
            await using var expire = connection.CreateCommand();
            expire.CommandText = "UPDATE scheduling.Outbox SET LeaseUntil=DATEADD(second,-1,SYSUTCDATETIME())";
            await expire.ExecuteNonQueryAsync(token);
        }
        handler.WaitForCancellation = false;
        await Dispatcher(connectionString, handler).DispatchAsync(token);
        await using var verify = new SqlConnection(connectionString);
        await verify.OpenAsync(token);
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT MessageId FROM scheduling.TestEffects";
        Assert.AreEqual(message.MessageId, (Guid)(await command.ExecuteScalarAsync(token))!);
    });

    [TestMethod]
    public Task TimedOutHandler_RollsBackAndSchedulesRetry() => WithDatabase(async (connectionString, token) =>
    {
        var handler = new TestHandler { WaitForCancellation = true };
        await EnqueueAsync(connectionString, handler, token);
        await Dispatcher(connectionString, handler, TimeSpan.FromMilliseconds(250)).DispatchAsync(token);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        Assert.AreEqual(0, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.TestEffects", token));
        Assert.AreEqual(1, await CountAsync(connection, "SELECT COUNT(*) FROM scheduling.Outbox WHERE State=0 AND Attempts=1 AND LastFailureCode='handler_timeout'", token));
    });

    private Task WithDatabase(Func<string, CancellationToken, Task> action) =>
        ReusableOutboxDatabase.ExecuteAsync((database, token) => action(database.ConnectionString, token), TestContext.CancellationToken);

    private Task WithIsolatedDatabase(Func<string, CancellationToken, Task> action) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            var contexts = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IDbContextFactory<SchedulingDbContext>>(provider);
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE scheduling.TestEffects (MessageId uniqueidentifier NOT NULL PRIMARY KEY)";
                await command.ExecuteNonQueryAsync(token);
            }
            await action(database.ConnectionString, token);
        }, TestContext.CancellationToken);

    private static async Task<OutboxMessage> EnqueueAsync(string connectionString, TestHandler handler, CancellationToken token)
    {
        var message = new OutboxMessage(Guid.CreateVersion7(), handler.MessageType, "{}");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await new OutboxWriter([handler]).WriteAsync(message, connection, transaction, token);
        await transaction.CommitAsync(token);
        return message;
    }

    private static OutboxDispatcher Dispatcher(string connectionString, TestHandler handler, TimeSpan? timeout = null) =>
        new(connectionString, [handler], NullLogger<OutboxDispatcher>.Instance, timeout);

    private static async Task<int> CountAsync(SqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(await command.ExecuteScalarAsync(token))!;
    }

    private sealed class TestHandler : IOutboxHandler
    {
        public string MessageType => "test.effect.v1";
        public bool Fail { get; set; }
        public bool ExpireLease { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO scheduling.TestEffects(MessageId) VALUES (@id)";
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = message.MessageId;
            await command.ExecuteNonQueryAsync(cancellationToken);
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (ExpireLease)
            {
                command.CommandText = "UPDATE scheduling.Outbox SET LeaseUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE MessageId=@id";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            if (Fail) throw new InvalidOperationException("Synthetic handler failure");
        }
    }
}
