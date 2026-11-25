using Wolverine;
using Wolverine.EntityFrameworkCore;
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
        options.UseRabbitMq(brokerConnectionString).AutoProvision();
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
}
