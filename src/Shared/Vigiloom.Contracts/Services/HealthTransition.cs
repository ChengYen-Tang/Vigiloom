using System.Data.Common;

namespace Vigiloom.Contracts.Services;

/// <summary>The final current result for one centrally evaluated health target.</summary>
public sealed record HealthTransition(Guid TargetId, Guid ServiceId, Guid? InstanceId, long Generation,
    ServiceHealthState State, DateTimeOffset ObservedAt, string ServiceName, string Environment);

public interface IHealthTransitionSink
{
    Task RecordAsync(HealthTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token);
}
