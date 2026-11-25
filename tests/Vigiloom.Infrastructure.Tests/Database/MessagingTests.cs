using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Vigiloom.Infrastructure.Messaging;
using Vigiloom.MessagingProbe;
using Vigiloom.Testing.SqlServer;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MessagingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task BestEffortUpdate_IsDeliveredWithoutDurableInbox()
    {
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker))
            Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ to run the isolated RabbitMQ integration test.");
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var id = Guid.CreateVersion7();
        var otherQueue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        Exception? failure = null;
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(broker!) };
            await using (var connection = await factory.CreateConnectionAsync(TestContext.CancellationToken))
            await using (var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.CancellationToken))
                await channel.QueueDeclareAsync(otherQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: TestContext.CancellationToken);
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
            {
                var builder = Host.CreateApplicationBuilder();
                var capture = new BestEffortCapture();
                builder.Services.AddSingleton(capture);
                builder.UseWolverine(options =>
                {
                    options.UseVigiloomMessaging(database.ConnectionString, broker!);
                    options.Discovery.DisableConventionalDiscovery();
                    options.Discovery.IncludeType<BestEffortHandler>();
                    options.PublishBestEffortTo<BestEffortMessage>(queue);
                    options.ListenBestEffortOn(queue);
                });
                using var host = builder.Build();
                await host.StartAsync(token);
                try
                {
                    await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new BestEffortMessage(id));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    Assert.AreEqual(id, await capture.Received.Task.WaitAsync(timeout.Token));
                    Assert.AreEqual(0, await CountAllIncomingAsync(database.ConnectionString, timeout.Token));
                    await using var verifyConnection = await factory.CreateConnectionAsync(timeout.Token);
                    await using var verifyChannel = await verifyConnection.CreateChannelAsync(cancellationToken: timeout.Token);
                    var other = await verifyChannel.QueueDeclarePassiveAsync(otherQueue, timeout.Token);
                    Assert.AreEqual(0U, other.MessageCount);
                }
                finally { await host.StopAsync(CancellationToken.None); }
            }, TestContext.CancellationToken);
        }
        catch (Exception exception) { failure = exception; }
        await DeleteQueuesAsync(broker!, [queue, otherQueue], failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public async Task SeparateProcess_ReceivesCommittedMessage()
    {
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker))
            Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ to run the isolated RabbitMQ integration test.");
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var id = Guid.CreateVersion7();
        Exception? failure = null;
        try
        {
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (senderDb, token) =>
            await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (receiverDb, innerToken) =>
            {
                await using (var setup = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseSqlServer(senderDb.ConnectionString).Options))
                    await setup.Database.EnsureCreatedAsync(innerToken);
                await using (var setup = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseSqlServer(receiverDb.ConnectionString).Options))
                    await setup.Database.EnsureCreatedAsync(innerToken);
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddDbContext<ProbeContext>(x => x.UseSqlServer(senderDb.ConnectionString));
                builder.UseWolverine(options =>
                {
                    options.UseVigiloomMessaging(senderDb.ConnectionString, broker!);
                    options.Discovery.DisableConventionalDiscovery();
                    options.PublishReliablyTo<ProbeMessage>(queue);
                });
                using var sender = builder.Build();
                await sender.StartAsync(innerToken);
                try
                {
                    await using (var scope = sender.Services.CreateAsyncScope())
                    {
                        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
                        var context = scope.ServiceProvider.GetRequiredService<ProbeContext>();
                        outbox.Enroll(context);
                        context.Receipts.Add(new ProbeReceipt { Id = id });
                        await outbox.PublishAsync(new ProbeMessage(id), new DeliveryOptions { DeduplicationId = id.ToString("N") });
                        await outbox.SaveChangesAndFlushMessagesAsync(innerToken);
                    }
                    var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                    var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Vigiloom.MessagingProbe/bin", configuration, "net10.0/Vigiloom.MessagingProbe.dll"));
                    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add(probe);
                    start.Environment["VIGILOOM_PROBE_DATABASE"] = receiverDb.ConnectionString;
                    start.Environment["VIGILOOM_PROBE_QUEUE"] = queue;
                    start.Environment["VIGILOOM_PROBE_MESSAGE"] = id.ToString("D");
                    start.Environment["VIGILOOM_TEST_RABBITMQ"] = broker!;
                    using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start messaging probe.");
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(innerToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(75));
                        await child.WaitForExitAsync(timeout.Token);
                        Assert.AreEqual(0, child.ExitCode);
                        await using var verify = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseSqlServer(receiverDb.ConnectionString).Options);
                        Assert.IsTrue(await verify.Receipts.AnyAsync(x => x.Id == id, timeout.Token));
                    }
                    finally
                    {
                        if (!child.HasExited)
                        {
                            child.Kill(entireProcessTree: true);
                            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            await child.WaitForExitAsync(cleanupTimeout.Token);
                        }
                    }
                }
                finally { await sender.StopAsync(CancellationToken.None); }
            }, token), TestContext.CancellationToken);
        }
        catch (Exception exception) { failure = exception; }
        await DeleteQueueAsync(broker!, queue, failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public async Task CommittedMessage_IsDeliveredAfterReceiverStarts()
    {
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker))
            Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ to run the isolated RabbitMQ integration test.");

        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var messageId = Guid.CreateVersion7();
        var rolledBackId = Guid.CreateVersion7();
        var retriedId = Guid.CreateVersion7();
        var failedId = Guid.CreateVersion7();
        var server = TestDatabaseServer.FromEnvironment();
        Exception? failure = null;
        try
        {
            await SqlTestDatabase.ExecuteAsync(server, async (senderDb, token) =>
            await SqlTestDatabase.ExecuteAsync(server, async (receiverDb, innerToken) =>
            {
                await using (var setup = CreateContext(senderDb.ConnectionString))
                    await setup.Database.EnsureCreatedAsync(innerToken);
                await using (var setup = CreateContext(receiverDb.ConnectionString))
                    await setup.Database.EnsureCreatedAsync(innerToken);

                using var sender = BuildHost(senderDb.ConnectionString, broker!, queue, receiver: false);
                await sender.StartAsync(innerToken);
                await using (var scope = sender.Services.CreateAsyncScope())
                {
                    var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
                    var context = scope.ServiceProvider.GetRequiredService<MessagingTestContext>();
                    outbox.Enroll(context);
                    context.Receipts.Add(new MessageReceipt { Id = rolledBackId, Marker = "duplicate" });
                    context.Receipts.Add(new MessageReceipt { Id = Guid.CreateVersion7(), Marker = "duplicate" });
                    await outbox.PublishAsync(new MessagingTestMessage(rolledBackId), new DeliveryOptions { DeduplicationId = rolledBackId.ToString("N") });
                    await Assert.ThrowsAsync<DbUpdateException>(() => outbox.SaveChangesAndFlushMessagesAsync(innerToken));
                }
                await using (var scope = sender.Services.CreateAsyncScope())
                {
                    var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
                    var context = scope.ServiceProvider.GetRequiredService<MessagingTestContext>();
                    outbox.Enroll(context);
                    context.Receipts.Add(new MessageReceipt { Id = messageId, Marker = messageId.ToString("N") });
                    await outbox.PublishAsync(new MessagingTestMessage(messageId), new DeliveryOptions { DeduplicationId = messageId.ToString("N") });
                    await outbox.SaveChangesAndFlushMessagesAsync(innerToken);
                }

                using var receiver = BuildHost(receiverDb.ConnectionString, broker!, queue, receiver: true);
                await receiver.StartAsync(innerToken);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(innerToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(75));
                    while (true)
                    {
                        await using var read = CreateContext(receiverDb.ConnectionString);
                        if (await read.Receipts.AnyAsync(x => x.Id == messageId, timeout.Token))
                        {
                            Assert.IsFalse(await read.Receipts.AnyAsync(x => x.Id == rolledBackId, timeout.Token));
                            break;
                        }
                        await Task.Delay(250, timeout.Token);
                    }
                    while (await CountIncomingAsync(receiverDb.ConnectionString, timeout.Token) < 1)
                        await Task.Delay(250, timeout.Token);
                    var before = await CountIncomingAsync(receiverDb.ConnectionString, timeout.Token);
                    await sender.Services.GetRequiredService<IMessageBus>().PublishAsync(
                        new MessagingTestMessage(messageId),
                        new DeliveryOptions { DeduplicationId = messageId.ToString("N") });
                    while (await CountIncomingAsync(receiverDb.ConnectionString, timeout.Token) <= before)
                        await Task.Delay(250, timeout.Token);
                    Assert.AreEqual(1, MessagingTestHandler.Attempts[messageId]);
                    await sender.Services.GetRequiredService<IMessageBus>().PublishAsync(
                        new MessagingTestMessage(retriedId, FailOnce: true),
                        new DeliveryOptions { DeduplicationId = retriedId.ToString("N") });
                    while (true)
                    {
                        await using var read = CreateContext(receiverDb.ConnectionString);
                        if (await read.Receipts.AnyAsync(x => x.Id == retriedId, timeout.Token)) break;
                        await Task.Delay(250, timeout.Token);
                    }
                    Assert.AreEqual(2, MessagingTestHandler.Attempts[retriedId]);
                    await sender.Services.GetRequiredService<IMessageBus>().PublishAsync(
                        new MessagingTestMessage(failedId, FailAlways: true),
                        new DeliveryOptions { DeduplicationId = failedId.ToString("N") });
                    while (await CountDeadLettersAsync(receiverDb.ConnectionString, timeout.Token) < 1)
                        await Task.Delay(250, timeout.Token);
                    Assert.IsGreaterThan(1, MessagingTestHandler.Attempts[failedId]);
                }
                finally
                {
                    await receiver.StopAsync(CancellationToken.None);
                    await sender.StopAsync(CancellationToken.None);
                }
            }, token), TestContext.CancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        await DeleteQueueAsync(broker!, queue, failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task DeleteQueueAsync(string broker, string queue, Exception? failure)
        => await DeleteQueuesAsync(broker, [queue], failure);

    private static async Task DeleteQueuesAsync(string broker, string[] queues, Exception? failure)
    {
        var failures = new List<Exception>();
        if (failure is not null) failures.Add(failure);
        foreach (var queue in queues)
        {
            try
            {
                var factory = new ConnectionFactory { Uri = new Uri(broker) };
                await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: CancellationToken.None);
                await channel.QueueDeleteAsync(queue, cancellationToken: CancellationToken.None);
            }
            catch (Exception cleanupFailure) { failures.Add(new InvalidOperationException($"Failed to delete test queue {queue}.", cleanupFailure)); }
        }
        if (failures.Count > (failure is null ? 0 : 1)) throw new AggregateException("Test queue cleanup failed.", failures);
    }

    private static async Task<int> CountIncomingAsync(string database, CancellationToken token)
    {
        await using var connection = new SqlConnection(database);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Handled'";
        return (int)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<int> CountDeadLettersAsync(string database, CancellationToken token)
    {
        await using var connection = new SqlConnection(database);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM wolverine.wolverine_dead_letters";
        return (int)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<int> CountAllIncomingAsync(string database, CancellationToken token)
    {
        await using var connection = new SqlConnection(database);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes";
        return (int)(await command.ExecuteScalarAsync(token))!;
    }

    private static IHost BuildHost(string database, string broker, string queue, bool receiver)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<MessagingTestContext>(x => x.UseSqlServer(database));
        builder.UseWolverine(options =>
        {
            options.UseVigiloomMessaging(database, broker);
            options.Discovery.DisableConventionalDiscovery();
            if (receiver)
            {
                options.Discovery.IncludeType<MessagingTestHandler>();
                options.ListenReliablyOn(queue);
            }
            else options.PublishReliablyTo<MessagingTestMessage>(queue);
        });
        return builder.Build();
    }

    private static MessagingTestContext CreateContext(string connection) =>
        new(new DbContextOptionsBuilder<MessagingTestContext>().UseSqlServer(connection).Options);
}

public sealed record MessagingTestMessage(Guid Id, bool FailOnce = false, bool FailAlways = false);
public sealed record BestEffortMessage(Guid Id);
public sealed class BestEffortCapture
{
    public TaskCompletionSource<Guid> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
public sealed class BestEffortHandler
{
    public static void Handle(BestEffortMessage message, BestEffortCapture capture) => capture.Received.TrySetResult(message.Id);
}
public sealed class MessageReceipt { public Guid Id { get; set; } public string Marker { get; set; } = ""; }

public sealed class MessagingTestContext(DbContextOptions<MessagingTestContext> options) : DbContext(options)
{
    public DbSet<MessageReceipt> Receipts => Set<MessageReceipt>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<MessageReceipt>().HasIndex(x => x.Marker).IsUnique();
}

public sealed class MessagingTestHandler
{
    public static ConcurrentDictionary<Guid, int> Attempts { get; } = new();

    [Deduplicated]
    [Transactional]
    public static async Task Handle(MessagingTestMessage message, MessagingTestContext context)
    {
        var attempts = Attempts.AddOrUpdate(message.Id, 1, (_, value) => value + 1);
        if (message.FailAlways || (message.FailOnce && attempts == 1)) throw new InvalidOperationException("Simulated failure");
        context.Receipts.Add(new MessageReceipt { Id = message.Id, Marker = message.Id.ToString("N") });
        await context.SaveChangesAsync();
    }
}
