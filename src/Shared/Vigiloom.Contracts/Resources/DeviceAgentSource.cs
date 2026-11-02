using System.Data.Common;

namespace Vigiloom.Contracts.Resources;

/// <summary>Server-side device lifecycle access for Agent authorization.</summary>
public interface IDeviceAgentSource
{
    Task<DeviceAgentState?> GetAsync(Guid deviceId, CancellationToken token);

    /// <summary>Locks the matching eligible device for the caller's same-database transaction.</summary>
    Task<bool> IsCurrentAsync(Guid deviceId, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token);
}

public sealed record DeviceAgentState(Guid DeviceId, long Generation, bool Eligible);
