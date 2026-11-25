using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Infrastructure.Messaging;
using Vigiloom.Testing.SqlServer;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MessagingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CommittedMessage_IsDeliveredAfterReceiverStarts()
    {
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker))
            Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ to run the isolated RabbitMQ integration test.");

        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var messageId = Guid.CreateVersion7();
        var server = TestDatabaseServer.FromEnvironment();
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
                context.Receipts.Add(new MessageReceipt { Id = messageId });
                await outbox.PublishAsync(new MessagingTestMessage(messageId));
                await outbox.SaveChangesAndFlushMessagesAsync(innerToken);
            }

            using var receiver = BuildHost(receiverDb.ConnectionString, broker!, queue, receiver: true);
            await receiver.StartAsync(innerToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(innerToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                while (true)
                {
                    await using var read = CreateContext(receiverDb.ConnectionString);
                    if (await read.Receipts.AnyAsync(x => x.Id == messageId, timeout.Token)) break;
                    await Task.Delay(250, timeout.Token);
                }
            }
            finally
            {
                await receiver.StopAsync(CancellationToken.None);
                await sender.StopAsync(CancellationToken.None);
            }
        }, token), TestContext.CancellationToken);
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

public sealed record MessagingTestMessage(Guid Id);
public sealed class MessageReceipt { public Guid Id { get; set; } }

public sealed class MessagingTestContext(DbContextOptions<MessagingTestContext> options) : DbContext(options)
{
    public DbSet<MessageReceipt> Receipts => Set<MessageReceipt>();
}

public sealed class MessagingTestHandler
{
    public static async Task Handle(MessagingTestMessage message, MessagingTestContext context)
    {
        context.Receipts.Add(new MessageReceipt { Id = message.Id });
        await context.SaveChangesAsync();
    }
}
