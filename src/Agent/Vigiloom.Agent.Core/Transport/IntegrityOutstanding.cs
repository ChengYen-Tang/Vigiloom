using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Transport;

public sealed record IntegrityOutstanding(Guid ScopeId, long ScopeRevision, long Generation, Guid SnapshotId);
public enum IntegrityReconcileResult { Pending, Completed, Rejected, OwnerChanged, OutstandingChanged }
internal sealed record IntegrityQueueMarker(Guid ScopeId, long ScopeRevision, long Generation, Guid SnapshotId, Guid PolicySaveToken, long? GapGeneration, byte[] Fingerprint);

public sealed partial class DurableBatchQueue
{
    private static void EnsureIntegrityOutstandingTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS integrity_outstanding (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1), scope_id TEXT NOT NULL,
                scope_revision INTEGER NOT NULL CHECK (scope_revision > 0), generation INTEGER NOT NULL CHECK (generation > 0),
                snapshot_id TEXT NOT NULL, gap_generation INTEGER NULL CHECK (gap_generation IS NULL OR gap_generation > 0),
                fingerprint BLOB NOT NULL CHECK (length(fingerprint) = 32));
            CREATE TABLE IF NOT EXISTS integrity_outstanding_batches (batch_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS integrity_retained_v6 (batch_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS integrity_generations (scope_id TEXT PRIMARY KEY, generation INTEGER NOT NULL
                CHECK (typeof(generation) = 'integer' AND generation > 0));
            CREATE TABLE IF NOT EXISTS integrity_gap_state (scope_id TEXT PRIMARY KEY, scope_revision INTEGER NOT NULL,
                generation INTEGER NOT NULL CHECK (generation > 0), CHECK (scope_revision > 0));
            """;
        command.ExecuteNonQuery();
    }

    public async Task<long> ReserveIntegrityGenerationAsync(AgentSettingsOwner owner, Guid scopeId, long centralHighWater, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (scopeId.Version != 7) throw new ArgumentException("A version 7 scope ID is required.", nameof(scopeId));
        if (centralHighWater is < 0 or long.MaxValue) throw new ArgumentOutOfRangeException(nameof(centralHighWater));
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            EnsureInventoryOutstandingTable(connection, transaction);
            EnsureIntegrityOutstandingTable(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO integrity_generations(scope_id, generation) VALUES ($scope, $floor + 1)
                ON CONFLICT(scope_id) DO UPDATE SET generation = MAX(generation, $floor) + 1
                WHERE typeof(generation) = 'integer' AND generation > 0 AND generation < 9223372036854775807
                RETURNING generation
                """;
            command.Parameters.AddWithValue("$scope", scopeId.ToString("D"));
            command.Parameters.AddWithValue("$floor", centralHighWater);
            if (command.ExecuteScalar() is not long generation) throw new InvalidOperationException("Integrity generation is exhausted.");
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return generation;
        }
        finally { gate.Release(); }
    }

    /// <summary>Durably records uncertain coverage before a scan or watcher recovery.</summary>
    public async Task<long> MarkIntegrityGapAsync(AgentSettingsOwner owner, Guid scopeId, long scopeRevision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (scopeId.Version != 7 || scopeRevision < 1) throw new ArgumentException("Invalid integrity scope.");
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            EnsureIntegrityOutstandingTable(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO integrity_gap_state(scope_id, scope_revision, generation) VALUES ($scope, $revision, 1)
                ON CONFLICT(scope_id) DO UPDATE SET scope_revision = $revision, generation = generation + 1
                WHERE typeof(generation) = 'integer' AND generation > 0 AND generation < 9223372036854775807
                    AND scope_revision <= $revision
                RETURNING generation
                """;
            command.Parameters.AddWithValue("$scope", scopeId.ToString("D"));
            command.Parameters.AddWithValue("$revision", scopeRevision);
            if (command.ExecuteScalar() is not long generation) throw new InvalidOperationException("Integrity gap generation is exhausted.");
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return generation;
        }
        finally { gate.Release(); }
    }

    public async Task<long?> ReadIntegrityGapAsync(AgentSettingsOwner owner, Guid scopeId, long scopeRevision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (scopeId.Version != 7 || scopeRevision < 1) throw new ArgumentException("Invalid integrity scope.");
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            EnsureIntegrityOutstandingTable(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT generation FROM integrity_gap_state WHERE scope_id = $scope AND scope_revision = $revision";
            command.Parameters.AddWithValue("$scope", scopeId.ToString("D"));
            command.Parameters.AddWithValue("$revision", scopeRevision);
            var result = command.ExecuteScalar() as long?;
            transaction.Commit();
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<IntegrityOutstanding?> ReadOutstandingIntegrityAsync(AgentSettingsOwner owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            EnsureIntegrityOutstandingTable(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT scope_id, scope_revision, generation, snapshot_id FROM integrity_outstanding WHERE singleton = 1";
            using var row = command.ExecuteReader();
            IntegrityOutstanding? result = null;
            if (row.Read())
            {
                if (!Guid.TryParse(row.GetString(0), out var scope) || scope.Version != 7 || row.GetInt64(1) < 1 || row.GetInt64(2) < 1 ||
                    !Guid.TryParse(row.GetString(3), out var snapshot) || snapshot.Version != 7)
                    throw new InvalidDataException("Outstanding integrity metadata is invalid.");
                result = new(scope, row.GetInt64(1), row.GetInt64(2), snapshot);
            }
            row.Close();
            transaction.Commit();
            return result;
        }
        finally { gate.Release(); }
    }

    /// <summary>Closes only a generation confirmed by the authenticated central status.</summary>
    public async Task<IntegrityReconcileResult> ReconcileIntegrityAsync(AgentSettingsOwner owner, IntegrityOutstanding expected,
        IntegrityStatusResult observed, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Kind != IntegrityStatusResultKind.Success || observed.Owner != owner || observed.Status?.ScopeId != expected.ScopeId)
            throw new ArgumentException("An authenticated status for the same owner and scope is required.", nameof(observed));
        var status = observed.Status;
        var policyObsolete = !status.ScopePresent || status.Enabled != true || status.CurrentRevision != expected.ScopeRevision;
        var exact = status.LastAttemptGeneration == expected.Generation;
        var terminal = exact && status.LastStatus is not null;
        if (!terminal && !policyObsolete && status.HighWaterGeneration <= expected.Generation) return IntegrityReconcileResult.Pending;
        var sameSnapshot = terminal && status.LastSnapshotId == expected.SnapshotId;
        var success = sameSnapshot && status.LastStatus is IntegrityCompletion.Complete or IntegrityCompletion.Partial;
        var reason = terminal && (!sameSnapshot || status.LastStatus == IntegrityCompletion.Rejected) ? QueueRejection.Conflict : QueueRejection.Expired;
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) return IntegrityReconcileResult.OwnerChanged;
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            EnsureIntegrityOutstandingTable(connection, transaction);
            long? queuedGap;
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT scope_id, scope_revision, generation, snapshot_id, gap_generation FROM integrity_outstanding WHERE singleton = 1";
                using var row = check.ExecuteReader();
                if (!row.Read() || !string.Equals(row.GetString(0), expected.ScopeId.ToString("D"), StringComparison.Ordinal) ||
                    row.GetInt64(1) != expected.ScopeRevision || row.GetInt64(2) != expected.Generation ||
                    !string.Equals(row.GetString(3), expected.SnapshotId.ToString("D"), StringComparison.Ordinal))
                    return IntegrityReconcileResult.OutstandingChanged;
                queuedGap = row.IsDBNull(4) ? null : row.GetInt64(4);
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = success ? """
                    UPDATE queue_state SET
                        queued_bytes = queued_bytes - COALESCE((SELECT SUM(length(payload)) FROM batches WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM integrity_outstanding_batches)), 0),
                        queued_count = queued_count - (SELECT COUNT(*) FROM batches WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM integrity_outstanding_batches))
                    WHERE singleton = 1;
                    DELETE FROM batches WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM integrity_outstanding_batches);
                    """ : """
                    UPDATE batches SET rejection = $reason WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM integrity_outstanding_batches);
                    """;
                if (!success) command.Parameters.AddWithValue("$reason", (int)reason);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "INSERT OR IGNORE INTO integrity_retained_v6(batch_id) SELECT batch_id FROM batches WHERE batch_id IN (SELECT batch_id FROM integrity_outstanding_batches)";
                command.ExecuteNonQuery();
                if (sameSnapshot && status.LastStatus == IntegrityCompletion.Complete && queuedGap is long gapGeneration)
                {
                    command.CommandText = "DELETE FROM integrity_gap_state WHERE scope_id = $scope AND scope_revision = $revision AND generation = $gap";
                    command.Parameters.AddWithValue("$scope", expected.ScopeId.ToString("D"));
                    command.Parameters.AddWithValue("$revision", expected.ScopeRevision);
                    command.Parameters.AddWithValue("$gap", gapGeneration);
                    command.ExecuteNonQuery();
                    command.Parameters.Clear();
                }
                command.CommandText = "DELETE FROM integrity_outstanding_batches; DELETE FROM integrity_outstanding WHERE singleton = 1";
                command.ExecuteNonQuery();
                CleanupRetainedInventoryAndRestoreVersion(connection, transaction);
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return success ? IntegrityReconcileResult.Completed : IntegrityReconcileResult.Rejected;
        }
        finally { gate.Release(); }
    }
}
