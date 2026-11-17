using System.Data.Common;

namespace Vigiloom.Contracts.Resources;

/// <summary>Reads a stable snapshot of the eligible devices in a group within the caller's transaction.</summary>
public interface IGroupAgentSource
{
    Task<IReadOnlyList<GroupAgentDevice>?> ReadAsync(Guid groupId, DbConnection connection, DbTransaction transaction, CancellationToken token);
}

public sealed record GroupAgentDevice(Guid DeviceId, long Generation, string Name);
