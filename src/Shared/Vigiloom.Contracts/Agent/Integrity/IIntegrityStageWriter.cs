using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Integrity;

/// <summary>Identity is derived from the authenticated Agent, never from the batch payload.</summary>
public sealed record IntegrityOwner(Guid AgentId, Guid DeviceId, long DeviceGeneration);
public enum IntegrityStageResult { Accepted = 1, Conflict = 2, Expired = 3, CapacityExceeded = 4, SnapshotTooLarge = 5 }
public enum IntegrityCompletion { Complete = 1, Partial = 2, Rejected = 3, Expired = 4 }

/// <summary>Stages within the caller's authenticated transaction; the caller owns commit and rollback.</summary>
public interface IIntegrityStageWriter
{
    Task<IntegrityStageResult> StageAsync(DbConnection connection, DbTransaction transaction, IntegrityOwner owner,
        IntegrityChunk chunk, DateTimeOffset databaseNow, CancellationToken token);
}
