using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Modules.NetworkMonitoring.Application;

/// <summary>Records the transition delivery in the same transaction as the Ping state.</summary>
public sealed class TemporalPingTransitionSink(OutboxWriter outbox, IConfiguration configuration) : IPingTransitionSink
{
    public Task RecordAsync(PingTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        if (connection is not SqlConnection sql || transaction is not SqlTransaction sqlTransaction)
            throw new InvalidOperationException("A SQL Server transaction is required.");
        var delivery = new PingTransitionDelivery(transition, transition.PreviousTransitionGeneration);
        var work = new ServiceWork(PingTransitionDelivery.Kind, JsonSerializer.Serialize(delivery));
        var request = new TemporalWorkRequest(configuration["Work:OperationsQueue"] ?? OperationsWork.Queue, work);
        return outbox.WriteAsync(new(Guid.CreateVersion7(), TemporalOutboxHandler.Type, JsonSerializer.Serialize(request)), sql, sqlTransaction, token);
    }
}
