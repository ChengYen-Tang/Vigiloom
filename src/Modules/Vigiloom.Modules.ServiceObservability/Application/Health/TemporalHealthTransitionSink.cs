using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Persists health notifications for delivery with the source state change.</summary>
public sealed class TemporalHealthTransitionSink(HttpClient resources, OutboxWriter outbox, IConfiguration configuration) : IHealthTransitionSink
{
    public async Task RecordAsync(HealthTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        if (connection is not SqlConnection sql || transaction is not SqlTransaction sqlTransaction)
            throw new InvalidOperationException("A SQL Server transaction is required.");
        var groups = await resources.GetFromJsonAsync<ResourceGroupSnapshot[]>(
            $"internal/v1/application-services/{transition.ServiceId:D}/groups", token)
            ?? throw new InvalidOperationException("The service group response was empty.");
        var delivery = new HealthTransitionDelivery(transition, transition.PreviousTransitionGeneration, groups);
        var work = new ServiceWork(HealthTransitionDelivery.Kind, JsonSerializer.Serialize(delivery));
        var request = new TemporalWorkRequest(configuration["Work:OperationsQueue"] ?? OperationsWork.Queue, work);
        await outbox.WriteAsync(new(Guid.CreateVersion7(), TemporalOutboxHandler.Type, JsonSerializer.Serialize(request)), sql, sqlTransaction, token);
    }
}
