using Microsoft.Data.SqlClient;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Worker.Hosting;

/// <summary>After cutover, Operations owns copied LINE deliveries.</summary>
internal sealed class LegacyLineDeliveryHandler(LineDeliveryHandler handler, IConfiguration configuration) : IOutboxHandler
{
    public string MessageType => LineDeliveryHandler.Type;

    public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"])
            ? handler.HandleAsync(message, connection, transaction, cancellationToken) : Task.CompletedTask;
}
