using System.Data.Common;
using OpenTelemetry.Proto.Trace.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Stages accepted trace summaries in the source transaction.</summary>
public interface ITraceTimelineSink
{
    Task RecordAsync(Guid serviceId, IEnumerable<Span> spans, DbConnection connection, DbTransaction transaction, CancellationToken token);
}
