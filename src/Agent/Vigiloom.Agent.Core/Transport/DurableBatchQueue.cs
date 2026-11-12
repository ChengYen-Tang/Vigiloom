using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent.Inventory;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>A persistent, bounded queue of immutable wire payloads belonging to one Agent identity.</summary>
public sealed partial class DurableBatchQueue(IdentityVault vault, Guid agentId, long? capacityBytes = null, int? maximumBatches = null)
{
    public const int MaximumBatchBytes = 1024 * 1024;
    public const int MaximumBatchRecords = 1000;
    // Leave headroom for a 24-hour backlog at one batch per second. The byte
    // limit remains authoritative and bursts can still reach either bound sooner.
    public const int MaximumQueuedBatches = 100_000;
    public const long DefaultCapacityBytes = 256L * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Persist an entire sample atomically, or leave the queue unchanged when capacity is insufficient. Keep the batches and payloads unchanged until the call completes.</summary>
    public Task<EnqueueResult> EnqueueManyAsync(IReadOnlyList<QueuedBatch> batches, CancellationToken token) =>
        EnqueueManyCoreAsync(batches, null, null, token);

    /// <summary>Atomically verify the registered owner and persist its sample under the same identity lease.</summary>
    public Task<EnqueueResult> EnqueueManyForOwnerAsync(IReadOnlyList<QueuedBatch> batches, AgentSettingsOwner owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return EnqueueManyCoreAsync(batches, owner, null, token);
    }

    /// <summary>Atomically queues one complete source generation and its durable outstanding marker.</summary>
    public Task<EnqueueResult> EnqueueInventoryForOwnerAsync(IReadOnlyList<QueuedBatch> batches, AgentSettingsOwner owner,
        InventorySource source, long generation, Guid snapshotId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(owner);
        if (!Enum.IsDefined(source) || generation < 1 || snapshotId.Version != 7 || batches.Count is < 1 or > InventoryChunkCodec.MaximumChunks)
            throw new ArgumentException("Invalid inventory generation.");
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < batches.Count; index++)
        {
            var batch = batches[index];
            if (batch is null || !InventoryChunkCodec.TryRead(batch.Payload, out var chunk) ||
                chunk!.BatchId != batch.BatchId || chunk.EnqueuedAt != batch.EnqueuedAt ||
                Math.Max(1, chunk.Items.Count) != batch.RecordCount || chunk.Source != source ||
                chunk.Generation != generation || chunk.SnapshotId != snapshotId || chunk.ChunkIndex != index || chunk.ChunkCount != batches.Count)
                throw new ArgumentException("Inventory batches do not form the specified generation.", nameof(batches));
            fingerprint.AppendData(SHA256.HashData(batch.Payload));
        }
        return EnqueueManyCoreAsync(batches, owner, new(source, generation, snapshotId, fingerprint.GetHashAndReset()), token);
    }

    private async Task<EnqueueResult> EnqueueManyCoreAsync(IReadOnlyList<QueuedBatch> batches, AgentSettingsOwner? owner,
        InventoryQueueMarker? inventory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0) throw new ArgumentException("At least one batch is required.", nameof(batches));
        if (batches.Count > MaximumQueuedBatches) throw new ArgumentOutOfRangeException(nameof(batches));
        if (capacityBytes is < 1) throw new InvalidOperationException("Queue capacity must be positive.");
        if (maximumBatches is < 1 or > MaximumQueuedBatches) throw new InvalidOperationException("Queue batch limit is outside the supported range.");
        var ids = new HashSet<Guid>();
        foreach (var batch in batches)
        {
            if (batch is null || batch.BatchId.Version != 7 || batch.RecordCount is < 1 or > MaximumBatchRecords ||
                batch.Payload is not { Length: > 0 and <= MaximumBatchBytes } || !ids.Add(batch.BatchId))
                throw new ArgumentException("Invalid or duplicate batch.", nameof(batches));
            if (inventory is null && IsInventoryPayload(batch.Payload))
                throw new ArgumentException("Inventory batches require the atomic inventory queue operation.", nameof(batches));
        }
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (owner is not null)
            {
                var identity = await session.ReadAsync(token);
                if (owner.AgentId != agentId || identity?.AgentId != agentId ||
                    !string.Equals(identity.Server, owner.Server, StringComparison.Ordinal) ||
                    !string.Equals(owner.StateDirectory, vault.StateDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return EnqueueResult.OwnerChanged;
            }
            var effective = capacityBytes is null || maximumBatches is null
                ? await AgentQueueCapacityStore.ReadEffectiveLockedAsync(session, agentId, token) : AgentQueueCapacity.Default;
            var byteLimit = capacityBytes ?? effective.PayloadBytes;
            var batchLimit = maximumBatches ?? effective.BatchCount;
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            if (inventory is not null)
            {
                EnsureInventoryOutstandingTable(connection, transaction);
                using var outstanding = connection.CreateCommand();
                outstanding.Transaction = transaction;
                outstanding.CommandText = "SELECT source, generation, snapshot_id, fingerprint FROM inventory_outstanding WHERE singleton = 1";
                using var reader = outstanding.ExecuteReader();
                if (reader.Read())
                    return reader.GetInt32(0) == (int)inventory.Source && reader.GetInt64(1) == inventory.Generation &&
                        string.Equals(reader.GetString(2), inventory.SnapshotId.ToString("D"), StringComparison.Ordinal)
                        ? CryptographicOperations.FixedTimeEquals((byte[])reader[3], inventory.Fingerprint) ? EnqueueResult.AlreadyQueued : EnqueueResult.ConflictingBatch
                        : EnqueueResult.InventoryOutstanding;
            }
            long newBytes = 0;
            var pending = new List<QueuedBatch>(batches.Count);
            using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT created_ticks, record_count, payload FROM batches WHERE batch_id = $id";
            var id = existing.Parameters.Add("$id", SqliteType.Text);
            foreach (var batch in batches)
            {
                token.ThrowIfCancellationRequested();
                id.Value = batch.BatchId.ToString("D");
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetInt64(0) != batch.EnqueuedAt.UtcTicks || reader.GetInt32(1) != batch.RecordCount ||
                        !batch.Payload.AsSpan().SequenceEqual((byte[])reader[2])) return EnqueueResult.ConflictingBatch;
                }
                else { pending.Add(batch); newBytes += batch.Payload.Length; }
            }
            if (inventory is not null && pending.Count != batches.Count) return EnqueueResult.ConflictingBatch;
            if (pending.Count == 0) return EnqueueResult.AlreadyQueued;
            // Waiting for queued batches to drain cannot make an oversized sample fit.
            if (newBytes > byteLimit || pending.Count > batchLimit) return EnqueueResult.ExceedsCapacity;
            using (var size = connection.CreateCommand())
            {
                size.Transaction = transaction;
                size.CommandText = "SELECT queued_bytes, queued_count FROM queue_state WHERE singleton = 1";
                using var reader = size.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("Agent queue metadata is missing.");
                if (newBytes > byteLimit - reader.GetInt64(0) || pending.Count > batchLimit - reader.GetInt64(1)) return EnqueueResult.Full;
            }
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO batches(batch_id, created_ticks, record_count, payload) VALUES ($id, $created, $count, $payload)";
            var insertId = insert.Parameters.Add("$id", SqliteType.Text);
            var created = insert.Parameters.Add("$created", SqliteType.Integer);
            var count = insert.Parameters.Add("$count", SqliteType.Integer);
            var payload = insert.Parameters.Add("$payload", SqliteType.Blob);
            foreach (var batch in pending)
            {
                token.ThrowIfCancellationRequested();
                insertId.Value = batch.BatchId.ToString("D");
                created.Value = batch.EnqueuedAt.UtcTicks;
                count.Value = batch.RecordCount;
                payload.Value = batch.Payload;
                insert.ExecuteNonQuery();
            }
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE queue_state SET queued_bytes = queued_bytes + $bytes, queued_count = queued_count + $count WHERE singleton = 1";
            update.Parameters.AddWithValue("$bytes", newBytes);
            update.Parameters.AddWithValue("$count", pending.Count);
            update.ExecuteNonQuery();
            if (inventory is not null)
            {
                using var insertOutstanding = connection.CreateCommand();
                insertOutstanding.Transaction = transaction;
                insertOutstanding.CommandText = "INSERT INTO inventory_outstanding(singleton, source, generation, snapshot_id, fingerprint) VALUES (1, $source, $generation, $snapshot, $fingerprint)";
                insertOutstanding.Parameters.AddWithValue("$source", (int)inventory.Source);
                insertOutstanding.Parameters.AddWithValue("$generation", inventory.Generation);
                insertOutstanding.Parameters.AddWithValue("$snapshot", inventory.SnapshotId.ToString("D"));
                insertOutstanding.Parameters.Add("$fingerprint", SqliteType.Blob).Value = inventory.Fingerprint;
                insertOutstanding.ExecuteNonQuery();
                using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "PRAGMA user_version";
                var currentVersion = Convert.ToInt32(versionCommand.ExecuteScalar());
                var requiredVersion = inventory.Source > InventorySource.HostVolumes ? 5 : 4;
                if (currentVersion < requiredVersion)
                {
                    versionCommand.CommandText = $"PRAGMA user_version = {requiredVersion}";
                    versionCommand.ExecuteNonQuery();
                }
                using var insertMember = connection.CreateCommand();
                insertMember.Transaction = transaction;
                insertMember.CommandText = "INSERT INTO inventory_outstanding_batches(batch_id) VALUES ($id)";
                var memberId = insertMember.Parameters.Add("$id", SqliteType.Text);
                foreach (var batch in batches)
                {
                    memberId.Value = batch.BatchId.ToString("D");
                    insertMember.ExecuteNonQuery();
                }
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return EnqueueResult.Queued;
        }
        finally { gate.Release(); }
    }

    /// <summary>Persist a batch before sending it. Keep payload unchanged until the call completes. A full queue rejects the batch for backpressure.</summary>
    public async Task<EnqueueResult> EnqueueAsync(Guid batchId, DateTimeOffset enqueuedAt, int recordCount,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (batchId.Version != 7) throw new ArgumentException("A version 7 batch ID is required.", nameof(batchId));
        if (recordCount is < 1 or > MaximumBatchRecords) throw new ArgumentOutOfRangeException(nameof(recordCount));
        if (payload.Length is < 1 or > MaximumBatchBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        if (IsInventoryPayload(payload.Span)) throw new ArgumentException("Inventory batches require the atomic inventory queue operation.", nameof(payload));
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
            CleanupRetainedInventoryAndRestoreVersion(connection, transaction);
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
            if (version is not (0 or 1 or 2 or 3 or 4 or 5)) throw new InvalidDataException("The Agent transport database format is not supported by this version.");
            if (version is 4 or 5)
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('inventory_outstanding', 'inventory_outstanding_batches', 'inventory_retained_batches')";
                if (Convert.ToInt64(command.ExecuteScalar()) != 3) throw new InvalidDataException("The Agent inventory queue format is incomplete.");
            }
            if (version == 5)
            {
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'inventory_retained_v5')";
                if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException("The Agent inventory version fence is incomplete.");
            }
            if (version is 1 or 2 or 3 or 4 or 5)
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

    private static bool IsInventoryPayload(ReadOnlySpan<byte> payload)
    {
        try
        {
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 8 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                var isVersion = reader.ValueTextEquals("schemaVersion"u8);
                if (!reader.Read()) return false;
                if (isVersion)
                {
                    if (reader.TokenType == JsonTokenType.Number) return reader.TryGetInt32(out var version) && version is InventoryChunkCodec.SchemaVersion or InventoryChunkCodec.ProcessSchemaVersion;
                    return reader.TokenType == JsonTokenType.String &&
                        int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is InventoryChunkCodec.SchemaVersion or InventoryChunkCodec.ProcessSchemaVersion;
                }
                reader.Skip();
            }
        }
        catch (JsonException) { }
        return false;
    }
}

public enum EnqueueResult { Queued, AlreadyQueued, ConflictingBatch, Full, OwnerChanged, ExceedsCapacity, InventoryOutstanding }
public enum QueueRejection { InvalidLocalBatch = 1, BadRequest = 2, Conflict = 3, PayloadTooLarge = 4, UnsupportedMediaType = 5, Expired = 6 }
public sealed record QueuedBatch(Guid BatchId, DateTimeOffset EnqueuedAt, int RecordCount, byte[] Payload);
public readonly record struct QueueStatus(long TotalBytes, long TotalBatches, long QuarantinedBatches);
