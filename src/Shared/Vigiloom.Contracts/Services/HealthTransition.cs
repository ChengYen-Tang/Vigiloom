using System.Data.Common;
using Vigiloom.Contracts.Resources;

namespace Vigiloom.Contracts.Services;

/// <summary>The final current result for one centrally evaluated health target.</summary>
public sealed record HealthTransition(Guid TargetId, Guid ServiceId, Guid? InstanceId, long Generation,
    ServiceHealthState State, DateTimeOffset ObservedAt, string ServiceName, string Environment, long PreviousTransitionGeneration = 0);

/// <summary>One health transition, its predecessor and the associated group snapshot.</summary>
public sealed record HealthTransitionDelivery(HealthTransition Transition, long PreviousTransitionGeneration,
    IReadOnlyList<ResourceGroupSnapshot> Groups)
{
    public const string Kind = "service.health-transition.v1";
}

public interface IHealthTransitionSink
{
    Task RecordAsync(HealthTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token);
}
