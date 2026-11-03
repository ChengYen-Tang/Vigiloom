using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;
using Vigiloom.Agent.Core.Configuration;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>A persistent, bounded queue of immutable wire payloads belonging to one Agent identity.</summary>
public sealed class DurableBatchQueue(IdentityVault vault, Guid agentId, long? capacityBytes = null, int? maximumBatches = null)
{
    public const int MaximumBatchBytes = 1024 * 1024;
    public const int MaximumBatchRecords = 1000;
    // Leave headroom for a 24-hour backlog at one batch per second. The byte
    // limit remains authoritative and bursts can still reach either bound sooner.
    public const int MaximumQueuedBatches = 100_000;
    public const long DefaultCapacityBytes = 256L * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Persist a batch before sending it. Keep payload unchanged until the call completes. A full queue rejects the batch for backpressure.</summary>
    public async Task<EnqueueResult> EnqueueAsync(Guid batchId, DateTimeOffset enqueuedAt, int recordCount,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (batchId.Version != 7) throw new ArgumentException("A version 7 batch ID is required.", nameof(batchId));
        if (recordCount is < 1 or > MaximumBatchRecords) throw new ArgumentOutOfRangeException(nameof(recordCount));
        if (payload.Length is < 1 or > MaximumBatchBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        if (capacityBytes is < 1) throw new InvalidOperationException("Queue capacity must be positive.");
        if (maximumBatches is < 1 or > MaximumQueuedBatches) throw new InvalidOperationException("Queue batch limit is outside the supported range.");
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await using var session = await vault.AcquireForTransportAsync(token);
            token.ThrowIfCancellationRequested();
            var effective = capacityBytes is null || maximumBatches is null
                ? await AgentQueueCapacityStore.ReadEffectiveLockedAsync(session, agentId, token) : AgentQueueCapacity.Default;
            var byteLimit = capacityBytes ?? effective.PayloadBytes;
            var batchLimit = maximumBatches ?? effective.BatchCount;
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT created_ticks, record_count, payload FROM batches WHERE batch_id = $id";
                existing.Parameters.AddWithValue("$id", batchId.ToString("D"));
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    var same = reader.GetInt64(0) == enqueuedAt.UtcTicks && reader.GetInt32(1) == recordCount &&
                        payload.Span.SequenceEqual((byte[])reader[2]);
                    return same ? EnqueueResult.AlreadyQueued : EnqueueResult.ConflictingBatch;
                }
            }
            using (var size = connection.CreateCommand())
            {
                size.Transaction = transaction;
                size.CommandText = "SELECT queued_bytes, queued_count FROM queue_state WHERE singleton = 1";
                using var reader = size.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("Agent queue metadata is missing.");
                if (payload.Length > byteLimit - reader.GetInt64(0) || reader.GetInt64(1) >= batchLimit) return EnqueueResult.Full;
            }
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO batches(batch_id, created_ticks, record_count, payload) VALUES ($id, $created, $count, $payload); UPDATE queue_state SET queued_bytes = queued_bytes + $bytes, queued_count = queued_count + 1 WHERE singleton = 1";
                insert.Parameters.AddWithValue("$id", batchId.ToString("D"));
                insert.Parameters.AddWithValue("$created", enqueuedAt.UtcTicks);
                insert.Parameters.AddWithValue("$count", recordCount);
                insert.Parameters.AddWithValue("$bytes", payload.Length);
                insert.Parameters.Add("$payload", SqliteType.Blob).Value = payload.ToArray();
                insert.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return EnqueueResult.Queued;
        }
        finally { gate.Release(); }
    }

    /// <summary>Read the oldest unacknowledged batch. Repeated reads return the same ID and bytes.</summary>
    public async Task<QueuedBatch?> PeekAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            token.ThrowIfCancellationRequested();
            using var connection = Open(session.DirectoryPath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT batch_id, created_ticks, record_count, payload FROM batches WHERE rejection IS NULL ORDER BY ordinal LIMIT 1";
            using var reader = command.ExecuteReader();
            return reader.Read() ? new QueuedBatch(Guid.Parse(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                reader.GetInt32(2), (byte[])reader[3]) : null;
        }
        finally { gate.Release(); }
    }

    /// <summary>Retain a permanently rejected batch for diagnosis without blocking newer batches.</summary>
    public async Task<bool> QuarantineAsync(Guid batchId, QueueRejection rejection, CancellationToken token)
    {
        if (!Enum.IsDefined(rejection)) throw new ArgumentOutOfRangeException(nameof(rejection));
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            token.ThrowIfCancellationRequested();
            using var connection = Open(session.DirectoryPath);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE batches SET rejection = $reason WHERE batch_id = $id AND rejection IS NULL";
            command.Parameters.AddWithValue("$id", batchId.ToString("D"));
            command.Parameters.AddWithValue("$reason", (int)rejection);
            return command.ExecuteNonQuery() == 1;
        }
        finally { gate.Release(); }
    }

    /// <summary>Return queue pressure including quarantined data, which still consumes capacity.</summary>
    public async Task<QueueStatus> GetStatusAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            token.ThrowIfCancellationRequested();
            return ReadStatusLocked(session);
        }
        finally { gate.Release(); }
    }

    // The caller must hold the identity lease. This permits a heartbeat to obtain
    // its sequence and queue snapshot under the same cross-process lock.
    internal QueueStatus ReadStatusLocked(IdentityVault.Session session) => ReadDiagnosticsLocked(session).Status;

    internal (QueueStatus Status, AgentQuarantineBreakdown Quarantine) ReadDiagnosticsLocked(IdentityVault.Session session)
    {
        using var connection = Open(session.DirectoryPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT q.queued_bytes, q.queued_count, COUNT(b.rejection),
                COALESCE(SUM(CASE WHEN b.rejection = 1 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN b.rejection = 2 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN b.rejection = 3 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN b.rejection = 4 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN b.rejection = 5 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN b.rejection = 6 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN b.rejection IS NOT NULL AND b.rejection NOT BETWEEN 1 AND 6 THEN 1 ELSE 0 END), 0)
            FROM queue_state AS q LEFT JOIN batches AS b ON b.rejection IS NOT NULL
            WHERE q.singleton = 1 GROUP BY q.queued_bytes, q.queued_count
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Agent queue metadata is missing.");
        var status = new QueueStatus(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        var breakdown = new AgentQuarantineBreakdown(reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9));
        return (status, breakdown);
    }

    /// <summary>Remove only a batch whose successful central commit has been acknowledged.</summary>
    public async Task<bool> AcknowledgeAsync(Guid batchId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            token.ThrowIfCancellationRequested();
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM batches WHERE batch_id = $id RETURNING length(payload)";
            command.Parameters.AddWithValue("$id", batchId.ToString("D"));
            if (command.ExecuteScalar() is not long bytes) return false;
            command.CommandText = "UPDATE queue_state SET queued_bytes = queued_bytes - $bytes, queued_count = queued_count - 1 WHERE singleton = 1";
            command.Parameters.AddWithValue("$bytes", bytes);
            command.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }
        finally { gate.Release(); }
    }

    private SqliteConnection Open(string directory)
    {
        if (agentId.Version != 7) throw new ArgumentException("A version 7 Agent ID is required.", nameof(agentId));
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "transport.db"),
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = (long)command.ExecuteScalar()!;
            if (version is not (0 or 1 or 2)) throw new InvalidDataException("The Agent transport database format is not supported by this version.");
            if (version is 1 or 2)
            {
                command.CommandText = "SELECT agent_id FROM queue_state WHERE singleton = 1";
                if (!string.Equals(command.ExecuteScalar() as string, agentId.ToString("D"), StringComparison.Ordinal))
                    throw new InvalidOperationException("The pending transport queue belongs to a different Agent identity.");
            }
            // The queue has one writer protected by the identity lease; rollback journaling
            // avoids retaining WAL files and FULL requests a durable transaction commit.
            command.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL";
            command.ExecuteNonQuery();
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE queue_state (singleton INTEGER PRIMARY KEY CHECK(singleton = 1), agent_id TEXT NOT NULL, queued_bytes INTEGER NOT NULL, queued_count INTEGER NOT NULL); INSERT INTO queue_state(singleton, agent_id, queued_bytes, queued_count) VALUES (1, $owner, 0, 0); CREATE TABLE batches (ordinal INTEGER PRIMARY KEY AUTOINCREMENT, batch_id TEXT NOT NULL UNIQUE, created_ticks INTEGER NOT NULL, record_count INTEGER NOT NULL, payload BLOB NOT NULL, rejection INTEGER NULL); PRAGMA user_version = 2";
                command.Parameters.AddWithValue("$owner", agentId.ToString("D"));
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            else if (version == 1)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "ALTER TABLE batches ADD COLUMN rejection INTEGER NULL; PRAGMA user_version = 2";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}

public enum EnqueueResult { Queued, AlreadyQueued, ConflictingBatch, Full }
public enum QueueRejection { InvalidLocalBatch = 1, BadRequest = 2, Conflict = 3, PayloadTooLarge = 4, UnsupportedMediaType = 5, Expired = 6 }
public sealed record QueuedBatch(Guid BatchId, DateTimeOffset EnqueuedAt, int RecordCount, byte[] Payload);
public readonly record struct QueueStatus(long TotalBytes, long TotalBatches, long QuarantinedBatches);
