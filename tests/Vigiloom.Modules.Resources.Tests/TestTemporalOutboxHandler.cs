using Microsoft.Data.SqlClient;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Modules.Resources.Tests;

internal sealed class TestTemporalOutboxHandler : IOutboxHandler
{
    public string MessageType => TemporalOutboxHandler.Type;
    public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
