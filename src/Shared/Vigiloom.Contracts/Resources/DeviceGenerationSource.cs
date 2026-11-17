using System.Data.Common;

namespace Vigiloom.Contracts.Resources;

/// <summary>Reads current device authorization generations without treating a temporary disable as revocation.</summary>
public interface IDeviceGenerationSource
{
    Task<IReadOnlyDictionary<Guid, long>> ReadAsync(Guid[] deviceIds, DbConnection connection, DbTransaction transaction, CancellationToken token);
}
