using System.Data.Common;

namespace Vigiloom.BuildingBlocks.Operations;

/// <summary>Append to the caller's transaction without owning or committing it.</summary>
public interface IOperationAuditWriter<in TEvent>
{
    Task AppendAsync(DbConnection connection, DbTransaction transaction, TEvent audit, CancellationToken cancellationToken);
}
