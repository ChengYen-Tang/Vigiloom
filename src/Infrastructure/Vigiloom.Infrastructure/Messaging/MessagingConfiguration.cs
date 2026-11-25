using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace Vigiloom.Infrastructure.Messaging;

public static class MessagingConfiguration
{
    /// <summary>
    /// Configures the service's durable message store and EF Core transaction integration.
    /// Each service supplies its own database and explicitly configures its routes and listeners.
    /// </summary>
    public static void UseVigiloomMessaging(this WolverineOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        options.PersistMessagesWithSqlServer(connectionString, "wolverine");
        options.UseEntityFrameworkCoreTransactions();
    }
}
