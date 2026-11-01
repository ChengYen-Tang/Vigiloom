using System.Data.Common;

namespace Vigiloom.Contracts.Resources;

/// <summary>Internal server-side module contract, not an HTTP or browser data service.</summary>
public interface IDevicePingSource
{
    IAsyncEnumerable<PingDeviceSnapshot> ReadEnabledAsync(CancellationToken token);
    /// <summary>Locks and verifies the owning device in the caller's same-database SQL transaction.</summary>
    Task<bool> IsCurrentAsync(Guid id, byte[] version, string target, DbConnection connection, DbTransaction transaction, CancellationToken token);
}

public sealed record PingDeviceSnapshot(Guid Id, byte[] Version, IReadOnlyList<string> Targets);
