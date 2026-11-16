using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Baseline;

/// <summary>Stores observations in the authenticated batch receipt transaction.</summary>
public interface IBaselineWriter
{
    Task WriteAsync(DbConnection connection, DbTransaction transaction, Guid agentId, Guid deviceId,
        long deviceGeneration, BaselineBatch batch, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}
