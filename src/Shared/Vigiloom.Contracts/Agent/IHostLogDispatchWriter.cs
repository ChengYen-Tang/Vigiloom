using System.Data.Common;

namespace Vigiloom.Contracts.Agent;

/// <summary>Identities of new events committed in one authenticated host log receipt.</summary>
public sealed record HostLogDispatch(Guid AgentId, Guid DeviceId, long DeviceGeneration, IReadOnlyList<Guid> MessageIds)
{
    public const string MessageType = "agent.host-log.received.v1";
}

/// <summary>Stages background work in the caller's transaction; commit remains with the receiver.</summary>
public interface IHostLogDispatchWriter
{
    Task StageAsync(DbConnection connection, DbTransaction transaction, HostLogDispatch dispatch, CancellationToken token);
}
