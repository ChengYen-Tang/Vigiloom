using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain.Health;

namespace Vigiloom.Observability;

internal sealed class HealthTimelineSink(IServiceScopeFactory scopes, IConfiguration configuration) : IHealthObservationSink
{
    public async Task RecordAsync(Guid serviceId, Guid targetId, HealthObservationKind kind, DateTimeOffset receivedAt,
        DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var id = Guid.CreateVersion7();
        var entry = new EventTimelineEntry("service-health", receivedAt, serviceId, "service",
            $"服務健康檢查：{(HealthCheckObservationKind)kind}", $"檢查 {targetId:D}", $"/services/{serviceId:D}");
        var work = new ServiceWork(EventTimelineWork.Kind, JsonSerializer.Serialize(new EventTimelineDelivery(id, entry)));
        var request = new TemporalWorkRequest(configuration["Work:OperationsQueue"] ?? OperationsWork.Queue, work);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxWriter>().WriteAsync(
            new(id, TemporalOutboxHandler.Type, JsonSerializer.Serialize(request)), (SqlConnection)connection, (SqlTransaction)transaction, token);
    }
}
