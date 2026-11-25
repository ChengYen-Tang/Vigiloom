using System.Text.Json;
using Microsoft.Data.SqlClient;
using Temporalio.Exceptions;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Starts a Temporal workflow for a work request saved with the source transaction.</summary>
public sealed class TemporalOutboxHandler(ServiceWorkClient client) : IOutboxHandler
{
    public const string Type = "temporal.service-work";
    public string MessageType => Type;

    public async Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<TemporalWorkRequest>(message.Payload)
            ?? throw new InvalidOperationException("The work request is empty.");
        try
        {
            await client.StartAsync(message.MessageId.ToString("D"), request.TaskQueue, request.Work, cancellationToken);
        }
        catch (WorkflowAlreadyStartedException)
        {
            // A previous attempt reached Temporal before its SQL acknowledgement committed.
        }
    }
}

/// <summary>Destination queue and compact work reference, persisted by the source service.</summary>
public sealed record TemporalWorkRequest(string TaskQueue, ServiceWork Work);
