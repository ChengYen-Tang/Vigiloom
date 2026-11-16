using System.Data.Common;

namespace Vigiloom.Contracts.Resources;

/// <summary>Reads service group membership in an existing resource transaction.</summary>
public interface IServiceGroupSource
{
    Task<IReadOnlyList<ResourceGroupSnapshot>> ReadAsync(Guid serviceId, DbConnection connection, DbTransaction transaction, CancellationToken token);
}
