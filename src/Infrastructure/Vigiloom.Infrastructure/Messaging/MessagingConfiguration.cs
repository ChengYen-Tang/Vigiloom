using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Wolverine.SqlServer;

namespace Vigiloom.Infrastructure.Messaging;

public static class MessagingConfiguration
{
    /// <summary>
    /// Configures the service's durable message store and EF Core transaction integration.
    /// Each service supplies its own database and explicitly configures its routes and listeners.
    /// </summary>
    public static void UseVigiloomMessaging(this WolverineOptions options, string connectionString, string brokerConnectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerConnectionString);

        options.PersistMessagesWithSqlServer(connectionString, "wolverine");
        options.UseEntityFrameworkCoreTransactions();
        options.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
        options.Durability.DeduplicationWindow = TimeSpan.FromDays(7);
        options.UseRabbitMq(brokerConnectionString).AutoProvision().DisableDeadLetterQueueing();
        options.OnException<Exception>().RetryWithCooldown(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    /// <summary>Routes a message requiring reliable delivery to one service's queue.</summary>
    public static void PublishReliablyTo<TMessage>(this WolverineOptions options, string queueName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        options.PublishMessage<TMessage>().ToRabbitQueue(queueName).UseDurableOutbox();
    }

    /// <summary>Enables durable reception for a service's own queue.</summary>
    public static void ListenReliablyOn(this WolverineOptions options, string queueName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        options.ListenToRabbitQueue(queueName).UseDurableInbox();
    }

    /// <summary>Routes replaceable updates without persisting the sender's outbox.</summary>
    public static void PublishBestEffortTo<TMessage>(this WolverineOptions options, string queueName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        options.PublishMessage<TMessage>().ToRabbitQueue(queueName).BufferedInMemory();
    }

    /// <summary>Receives replaceable updates without persisting the receiver's inbox.</summary>
    public static void ListenBestEffortOn(this WolverineOptions options, string queueName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        options.ListenToRabbitQueue(queueName).BufferedInMemory();
    }
}
