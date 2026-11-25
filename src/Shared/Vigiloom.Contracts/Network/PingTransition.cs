using System.Data.Common;
using Vigiloom.Contracts.Resources;

namespace Vigiloom.Contracts.Network;

/// <summary>One committed target state change; generation identifies the measurement for that target.</summary>
public sealed record PingTransition(Guid DeviceId, string Target, long Generation, DateTimeOffset OccurredAt,
    Reachability Previous, Reachability Current, PingDeviceContext Device);

/// <summary>One transition and the preceding transition's generation for the same target.</summary>
public sealed record PingTransitionDelivery(PingTransition Transition, long PreviousTransitionGeneration)
{
    public const string Kind = "network.ping-transition.v1";
}

/// <summary>Records local consequences of a transition in the same database transaction.</summary>
public interface IPingTransitionSink
{
    Task RecordAsync(PingTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token);
}
