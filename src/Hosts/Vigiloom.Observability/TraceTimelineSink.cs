using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Observability;

internal sealed class TraceTimelineSink(OutboxWriter outbox, IConfiguration configuration) : ITraceTimelineSink
{
    public async Task RecordAsync(Guid serviceId, IEnumerable<Span> spans, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        foreach (var batch in spans.Chunk(32))
        {
            var items = batch.Select(span => new EventTimelineDelivery(Guid.CreateVersion7(),
                new EventTimelineEntry("service-trace", DateTimeOffset.UnixEpoch.AddTicks((long)(span.StartTimeUnixNano / 100)),
                    serviceId, "service", "應用程式追蹤", $"Trace {Convert.ToHexStringLower(span.TraceId.Span)}；Span {Convert.ToHexStringLower(span.SpanId.Span)}", $"/services/{serviceId:D}"))).ToArray();
            var id = Guid.CreateVersion7();
            var work = new ServiceWork(EventTimelineWork.BatchKind, JsonSerializer.Serialize(new EventTimelineBatch(items)));
            var request = new TemporalWorkRequest(configuration["Work:OperationsQueue"] ?? OperationsWork.Queue, work);
            await outbox.WriteAsync(new(id, TemporalOutboxHandler.Type, JsonSerializer.Serialize(request)), (SqlConnection)connection, (SqlTransaction)transaction, token);
        }
    }
}
