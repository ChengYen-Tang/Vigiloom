using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Core.Transport;

public sealed record InventoryOutstanding(InventorySource Source, long Generation, Guid SnapshotId);
public enum InventoryReconcileResult { Pending, Completed, Rejected, OwnerChanged, OutstandingChanged }
internal sealed record InventoryQueueMarker(InventorySource Source, long Generation, Guid SnapshotId, byte[] Fingerprint);

public sealed partial class DurableBatchQueue
{
    private static void EnsureInventoryOutstandingTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS inventory_outstanding (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1), source INTEGER NOT NULL,
                generation INTEGER NOT NULL CHECK (generation > 0), snapshot_id TEXT NOT NULL,
                fingerprint BLOB NOT NULL CHECK (length(fingerprint) = 32));
            CREATE TABLE IF NOT EXISTS inventory_outstanding_batches (batch_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS inventory_retained_batches (batch_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS inventory_retained_v5 (batch_id TEXT PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<InventoryOutstanding?> ReadOutstandingInventoryAsync(AgentSettingsOwner owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            EnsureInventoryOutstandingTable(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT source, generation, snapshot_id FROM inventory_outstanding WHERE singleton = 1";
            using var reader = command.ExecuteReader();
            InventoryOutstanding? result = null;
            if (reader.Read())
            {
                var source = (InventorySource)reader.GetInt32(0);
                var generation = reader.GetInt64(1);
                if (!Enum.IsDefined(source) || generation < 1 || !Guid.TryParse(reader.GetString(2), out var snapshot) || snapshot.Version != 7)
                    throw new InvalidDataException("Outstanding inventory metadata is invalid.");
                result = new(source, generation, snapshot);
            }
            reader.Close();
            transaction.Commit();
            return result;
        }
        finally { gate.Release(); }
    }

    /// <summary>Closes only the exact generation confirmed by an authenticated central status.</summary>
    public async Task<InventoryReconcileResult> ReconcileInventoryAsync(AgentSettingsOwner owner, InventoryOutstanding expected,
        InventoryStatusResult observed, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Kind != InventoryStatusResultKind.Success || observed.Owner != owner || observed.Sources is null)
            throw new ArgumentException("An authenticated status for the same owner is required.", nameof(observed));
        var status = observed.Sources.SingleOrDefault(x => x.Source == expected.Source);
        var exact = status?.LastAttemptGeneration == expected.Generation;
        var terminal = exact && status!.LastStatus is not null;
        if (!terminal && (status is null || status.HighWaterGeneration <= expected.Generation)) return InventoryReconcileResult.Pending;
        var sameSnapshot = terminal && status!.LastSnapshotId == expected.SnapshotId;
        var success = sameSnapshot && status!.LastStatus is InventoryCompletion.Complete or InventoryCompletion.Partial;
        var reason = sameSnapshot && status?.LastRejectionReason == InventoryRejectionReason.Capacity ? QueueRejection.PayloadTooLarge :
            terminal && (!sameSnapshot || status?.LastStatus == InventoryCompletion.Rejected) ? QueueRejection.Conflict : QueueRejection.Expired;
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) return InventoryReconcileResult.OwnerChanged;
            using var connection = Open(session.DirectoryPath);
            using var transaction = connection.BeginTransaction();
            EnsureInventoryOutstandingTable(connection, transaction);
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT source, generation, snapshot_id FROM inventory_outstanding WHERE singleton = 1";
                using var row = check.ExecuteReader();
                if (!row.Read() || row.GetInt32(0) != (int)expected.Source || row.GetInt64(1) != expected.Generation ||
                    !string.Equals(row.GetString(2), expected.SnapshotId.ToString("D"), StringComparison.Ordinal))
                    return InventoryReconcileResult.OutstandingChanged;
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = success ? """
                    UPDATE queue_state SET
                        queued_bytes = queued_bytes - COALESCE((SELECT SUM(length(payload)) FROM batches WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM inventory_outstanding_batches)), 0),
                        queued_count = queued_count - (SELECT COUNT(*) FROM batches WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM inventory_outstanding_batches))
                    WHERE singleton = 1;
                    DELETE FROM batches WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM inventory_outstanding_batches);
                    """ : """
                    UPDATE batches SET rejection = $reason WHERE rejection IS NULL AND batch_id IN (SELECT batch_id FROM inventory_outstanding_batches);
                    """;
                if (!success) command.Parameters.AddWithValue("$reason", (int)reason);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "INSERT OR IGNORE INTO inventory_retained_batches(batch_id) SELECT batch_id FROM batches WHERE batch_id IN (SELECT batch_id FROM inventory_outstanding_batches)";
                command.ExecuteNonQuery();
                if (expected.Source > InventorySource.HostVolumes)
                {
                    command.CommandText = "INSERT OR IGNORE INTO inventory_retained_v5(batch_id) SELECT batch_id FROM batches WHERE batch_id IN (SELECT batch_id FROM inventory_outstanding_batches)";
                    command.ExecuteNonQuery();
                }
                command.CommandText = "DELETE FROM inventory_outstanding_batches; DELETE FROM inventory_outstanding WHERE singleton = 1";
                command.ExecuteNonQuery();
                CleanupRetainedInventoryAndRestoreVersion(connection, transaction);
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return success ? InventoryReconcileResult.Completed : InventoryReconcileResult.Rejected;
        }
        finally { gate.Release(); }
    }

    private static void CleanupRetainedInventoryAndRestoreVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt64(command.ExecuteScalar());
        if (version is not (4 or 5 or 6)) return;
        if (version == 6)
        {
            command.CommandText = "DELETE FROM integrity_retained_v6 WHERE batch_id NOT IN (SELECT batch_id FROM batches); SELECT EXISTS(SELECT 1 FROM integrity_outstanding) OR EXISTS(SELECT 1 FROM integrity_retained_v6)";
            if (Convert.ToInt32(command.ExecuteScalar()) != 0) return;
        }
        command.CommandText = "CREATE TABLE IF NOT EXISTS inventory_retained_v5 (batch_id TEXT PRIMARY KEY); DELETE FROM inventory_retained_batches WHERE batch_id NOT IN (SELECT batch_id FROM batches); DELETE FROM inventory_retained_v5 WHERE batch_id NOT IN (SELECT batch_id FROM batches)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM inventory_outstanding WHERE source > 12) OR EXISTS(SELECT 1 FROM inventory_retained_v5)";
        var requiredVersion = Convert.ToInt32(command.ExecuteScalar()) != 0 ? 5 : 0;
        if (requiredVersion == 5)
        {
            if (version == 6) { command.CommandText = "PRAGMA user_version = 5"; command.ExecuteNonQuery(); }
            return;
        }
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM inventory_outstanding) OR EXISTS(SELECT 1 FROM inventory_retained_batches)";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) requiredVersion = 4;
        if (requiredVersion == 4)
        {
            if (version is 5 or 6) { command.CommandText = "PRAGMA user_version = 4"; command.ExecuteNonQuery(); }
            return;
        }
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'log_sources')";
        var hasLogSchema = Convert.ToInt32(command.ExecuteScalar()) != 0;
        command.CommandText = hasLogSchema ? "PRAGMA user_version = 3" : "PRAGMA user_version = 2";
        command.ExecuteNonQuery();
    }
}
