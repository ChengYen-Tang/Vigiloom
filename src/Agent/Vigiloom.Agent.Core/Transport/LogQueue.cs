using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Transport;

public sealed partial class DurableBatchQueue
{
    private const int MaximumCursorCharacters = 4096;

    /// <summary>Reads the source generation and last durably queued position for the current owner.</summary>
    public async Task<LogCursor?> ReadLogCursorAsync(AgentSettingsOwner owner, Guid sourceId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateSource(sourceId);
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            EnsureLogSchema(connection);
            var state = ReadLogSource(connection, null, sourceId);
            return state is null ? null : new(state.Generation, state.Cursor);
        }
        finally { gate.Release(); }
    }

    /// <summary>CAS-bind a source configuration generation. A source ID denotes the same native source; replacing it requires a new ID.</summary>
    public async Task<bool> BindLogSourceAsync(AgentSettingsOwner owner, Guid sourceId, Guid? expectedGeneration, Guid generation,
        bool resetCursor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateSource(sourceId);
        if (generation.Version != 7 || expectedGeneration is { Version: not 7 }) throw new ArgumentException("Source generations must be version 7 UUIDs.");
        if (resetCursor && expectedGeneration == generation) throw new ArgumentException("Resetting a cursor requires a new generation.");
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            EnsureLogSchema(connection);
            using var transaction = connection.BeginTransaction();
            var current = ReadLogSource(connection, transaction, sourceId);
            if (current?.Generation != expectedGeneration) return false;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            command.Parameters.AddWithValue("$generation", generation.ToString("D"));
            if (expectedGeneration is null)
                command.CommandText = "INSERT INTO log_sources(source_id, generation) VALUES ($source, $generation) ON CONFLICT(source_id) DO NOTHING";
            else
            {
                command.CommandText = """
                    UPDATE log_sources SET generation = $generation, cursor = CASE WHEN $reset = 1 THEN NULL ELSE cursor END,
                        last_batch_id = CASE WHEN generation = $generation THEN last_batch_id ELSE NULL END,
                        last_digest = CASE WHEN generation = $generation THEN last_digest ELSE NULL END
                    WHERE source_id = $source AND generation = $expected
                    """;
                command.Parameters.AddWithValue("$expected", expectedGeneration.Value.ToString("D"));
                command.Parameters.AddWithValue("$reset", resetCursor ? 1 : 0);
            }
            var changed = command.ExecuteNonQuery() == 1;
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return changed;
        }
        finally { gate.Release(); }
    }

    /// <summary>Commit the immutable batch and source position together; full queues never advance the cursor.</summary>
    public async Task<LogEnqueueResult> EnqueueLogAsync(AgentSettingsOwner owner, Guid sourceId, Guid generation,
        string? expectedCursor, string nextCursor, QueuedBatch batch, CancellationToken token)
        => await EnqueueLogAsync(owner, sourceId, generation, expectedCursor, nextCursor, batch, null, token);

    /// <summary>When supplied, the saved settings token fences changes under the same identity lease as queue commit.</summary>
    public async Task<LogEnqueueResult> EnqueueLogAsync(AgentSettingsOwner owner, Guid sourceId, Guid generation,
        string? expectedCursor, string nextCursor, QueuedBatch batch, Guid? settingsSaveToken, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(batch);
        ValidateSource(sourceId);
        if (generation.Version != 7 || settingsSaveToken is { Version: not 7 } || !ValidCursor(expectedCursor, true) || !ValidCursor(nextCursor, false) ||
            string.Equals(expectedCursor, nextCursor, StringComparison.Ordinal)) throw new ArgumentException("Invalid source generation or cursor.");
        if (!AgentLogBatchCodec.TryRead(batch.Payload, out var parsed) || parsed!.SourceId != sourceId ||
            parsed.BatchId != batch.BatchId || parsed.EnqueuedAt != batch.EnqueuedAt || parsed.Items.Count != batch.RecordCount)
            throw new ArgumentException("Batch does not match its source and queue metadata.", nameof(batch));
        if (capacityBytes is < 1 || maximumBatches is < 1 or > MaximumQueuedBatches) throw new InvalidOperationException("Queue capacity is outside the supported range.");
        var digest = SHA256.HashData(batch.Payload);
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) return LogEnqueueResult.OwnerChanged;
            if (settingsSaveToken is { } expectedSettings)
            {
                var saved = await session.ReadLogSettingsAsync(token);
                if (saved is null || saved.SaveToken != expectedSettings || saved.AgentId != owner.AgentId ||
                    !string.Equals(saved.Server, owner.Server, StringComparison.Ordinal) || saved.Snapshot is null || !saved.Snapshot.IsValid() ||
                    parsed!.Items.Count > saved.Snapshot.MaxBatchRecords ||
                    !saved.Snapshot.Sources.Any(source => source.SourceId == sourceId && source.Generation == generation && source.Enabled &&
                        source.Kind == parsed!.SourceKind && source.ParserVersion == parsed.ParserVersion &&
                        string.Equals(source.Name, parsed.SourceName, StringComparison.Ordinal))) return LogEnqueueResult.SettingsChanged;
            }
            var effective = capacityBytes is null || maximumBatches is null
                ? await AgentQueueCapacityStore.ReadEffectiveLockedAsync(session, agentId, token) : AgentQueueCapacity.Default;
            var byteLimit = capacityBytes ?? effective.PayloadBytes;
            var batchLimit = maximumBatches ?? effective.BatchCount;
            using var connection = Open(session.DirectoryPath);
            EnsureLogSchema(connection);
            using var transaction = connection.BeginTransaction();
            var state = ReadLogSource(connection, transaction, sourceId);
            if (state is null || state.Generation != generation) return LogEnqueueResult.CursorConflict;
            if (!string.Equals(state.Cursor, expectedCursor, StringComparison.Ordinal))
            {
                return string.Equals(state.Cursor, nextCursor, StringComparison.Ordinal) && state.LastBatchId == batch.BatchId &&
                    state.LastDigest is not null && CryptographicOperations.FixedTimeEquals(state.LastDigest, digest)
                    ? LogEnqueueResult.AlreadyCommitted : LogEnqueueResult.CursorConflict;
            }
            using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT 1 FROM batches WHERE batch_id = $id";
            existing.Parameters.AddWithValue("$id", batch.BatchId.ToString("D"));
            if (existing.ExecuteScalar() is not null) return LogEnqueueResult.BatchConflict;
            if (batch.Payload.Length > byteLimit || batchLimit < 1) return LogEnqueueResult.ExceedsCapacity;
            using var size = connection.CreateCommand();
            size.Transaction = transaction;
            size.CommandText = "SELECT queued_bytes, queued_count FROM queue_state WHERE singleton = 1";
            using (var row = size.ExecuteReader())
            {
                if (!row.Read()) throw new InvalidDataException("Agent queue metadata is missing.");
                if (batch.Payload.Length > byteLimit - row.GetInt64(0) || row.GetInt64(1) >= batchLimit) return LogEnqueueResult.Full;
            }
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO batches(batch_id, created_ticks, record_count, payload) VALUES ($id, $created, $count, $payload);
                UPDATE queue_state SET queued_bytes = queued_bytes + $bytes, queued_count = queued_count + 1 WHERE singleton = 1;
                UPDATE log_sources SET cursor = $next, last_batch_id = $id, last_digest = $digest WHERE source_id = $source;
                """;
            insert.Parameters.AddWithValue("$id", batch.BatchId.ToString("D"));
            insert.Parameters.AddWithValue("$created", batch.EnqueuedAt.UtcTicks);
            insert.Parameters.AddWithValue("$count", batch.RecordCount);
            insert.Parameters.Add("$payload", SqliteType.Blob).Value = batch.Payload;
            insert.Parameters.AddWithValue("$bytes", batch.Payload.Length);
            insert.Parameters.AddWithValue("$next", nextCursor);
            insert.Parameters.Add("$digest", SqliteType.Blob).Value = digest;
            insert.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            insert.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return LogEnqueueResult.Queued;
        }
        finally { gate.Release(); }
    }

    private async Task<bool> IsOwnerAsync(IdentityVault.Session session, AgentSettingsOwner owner, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        return owner.AgentId == agentId && identity?.AgentId == agentId &&
            string.Equals(identity.Server, owner.Server, StringComparison.Ordinal) &&
            string.Equals(owner.StateDirectory, vault.StateDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static LogSourceState? ReadLogSource(SqliteConnection connection, SqliteTransaction? transaction, Guid sourceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation, cursor, last_batch_id, last_digest FROM log_sources WHERE source_id = $source";
        command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetValue(0) is not string generationText || !Guid.TryParseExact(generationText, "D", out var generation) || generation.Version != 7)
            throw new InvalidDataException("Invalid log source generation.");
        var cursorValue = reader.GetValue(1);
        if (cursorValue is not DBNull && (cursorValue is not string position || !ValidCursor(position, false)))
            throw new InvalidDataException("Invalid log source cursor.");
        var batchValue = reader.GetValue(2);
        var digestValue = reader.GetValue(3);
        Guid? batchId = null;
        byte[]? digest = null;
        if (batchValue is not DBNull || digestValue is not DBNull)
        {
            if (batchValue is not string batchText || !Guid.TryParseExact(batchText, "D", out var parsed) || parsed.Version != 7 ||
                digestValue is not byte[] { Length: 32 } bytes || cursorValue is DBNull)
                throw new InvalidDataException("Invalid log source commit marker.");
            batchId = parsed;
            digest = bytes;
        }
        return new(generation, cursorValue is DBNull ? null : (string)cursorValue, batchId, digest);
    }

    private sealed record LogSourceState(Guid Generation, string? Cursor, Guid? LastBatchId, byte[]? LastDigest);

    private static void EnsureLogSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = (long)command.ExecuteScalar()!;
        if (version == 3) return;
        if (version == 4)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'log_sources')";
            if (Convert.ToInt32(command.ExecuteScalar()) != 0) return;
        }
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE log_sources (source_id TEXT PRIMARY KEY NOT NULL, generation TEXT NOT NULL, cursor TEXT NULL,
                last_batch_id TEXT NULL, last_digest BLOB NULL,
                CHECK ((last_batch_id IS NULL AND last_digest IS NULL) OR (last_batch_id IS NOT NULL AND last_digest IS NOT NULL AND length(last_digest) = 32)));
            """;
        command.ExecuteNonQuery();
        if (version != 4)
        {
            command.CommandText = "PRAGMA user_version = 3";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void ValidateSource(Guid sourceId)
    {
        if (sourceId.Version != 7) throw new ArgumentException("A version 7 source ID is required.", nameof(sourceId));
    }

    private static bool ValidCursor(string? cursor, bool nullable) => cursor is null ? nullable :
        cursor.Length is > 0 and <= MaximumCursorCharacters && !cursor.Contains('\0');
}

public sealed record LogCursor(Guid Generation, string? Position);
public enum LogEnqueueResult { Queued, AlreadyCommitted, Full, ExceedsCapacity, OwnerChanged, CursorConflict, BatchConflict, SettingsChanged }
