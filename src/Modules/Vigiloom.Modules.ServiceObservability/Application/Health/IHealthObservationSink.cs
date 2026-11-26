using System.Data.Common;
using Vigiloom.Modules.ServiceObservability.Domain.Health;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Records a committed health observation for downstream consumers.</summary>
public interface IHealthObservationSink
{
    Task RecordAsync(Guid serviceId, Guid targetId, HealthObservationKind kind, DateTimeOffset receivedAt,
        DbConnection connection, DbTransaction transaction, CancellationToken token);
}
