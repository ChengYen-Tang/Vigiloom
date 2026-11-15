namespace Vigiloom.Contracts.Agent;

/// <summary>Reads committed host events by their authenticated Agent and stable message identities.</summary>
public interface IHostLogReader
{
    Task<IReadOnlyList<HostLogEntry>> ReadAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token);
}
