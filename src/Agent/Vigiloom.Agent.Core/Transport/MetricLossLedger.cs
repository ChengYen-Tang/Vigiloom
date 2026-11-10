using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>Fixed-size, identity-bound accounting for abandoned metric records.</summary>
public sealed class MetricLossLedger : IAsyncDisposable
{
    private readonly IdentityVault vault;
    private readonly AgentSettingsOwner owner;
    private readonly IdentityVault.MetricOwnership ownership;
    private readonly bool ownsLease;
    private readonly Guid writer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    private bool completed;
    public Guid WriterId => writer;

    private MetricLossLedger(IdentityVault vault, AgentSettingsOwner owner, IdentityVault.MetricOwnership ownership, Guid writer, bool ownsLease)
        => (this.vault, this.owner, this.ownership, this.writer, this.ownsLease) = (vault, owner, ownership, writer, ownsLease);

    /// <summary>Acquire the single-producer lease and durably start a new writer session.</summary>
    public static async Task<MetricLossLedger> StartAsync(IdentityVault vault, AgentSettingsOwner owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(owner);
        var ownership = vault.AcquireMetricOwnership();
        try
        {
            var ledger = new MetricLossLedger(vault, owner, ownership, Guid.CreateVersion7(), true);
            await ledger.StartSessionAsync(false, token);
            return ledger;
        }
        catch
        {
            await ownership.DisposeAsync();
            throw;
        }
    }

    /// <summary>Start accounting while the caller retains its producer lease across storage failures.</summary>
    public static async Task<MetricLossLedger> StartUnderOwnershipAsync(IdentityVault vault, AgentSettingsOwner owner,
        IdentityVault.MetricOwnership ownership, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(ownership);
        if (!ownership.IsHeldFor(vault)) throw new InvalidOperationException("Metric producer ownership is not held.");
        var ledger = new MetricLossLedger(vault, owner, ownership, Guid.CreateVersion7(), false);
        await ledger.StartSessionAsync(true, token);
        return ledger;
    }

    private async Task StartSessionAsync(bool markUnknown, CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        await VerifyOwnerAsync(session, token);
        using var connection = Open(session.DirectoryPath, owner);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE loss_state SET unknown = CASE WHEN writer IS NULL AND $forceUnknown = 0 THEN unknown ELSE 1 END, writer = $writer, sequence = 0, last_batches = 0, last_records = 0 WHERE singleton = 1";
        command.Parameters.AddWithValue("$writer", writer.ToString("D"));
        command.Parameters.AddWithValue("$forceUnknown", markUnknown ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Metric loss state is missing.");
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    /// <summary>Read cumulative totals; incomplete accounting is not a zero-loss claim.</summary>
    public Task<MetricLossSnapshot> ReadAsync(CancellationToken token) => ExecuteAsync(ReadCoreAsync, token);

    private async Task<MetricLossSnapshot> ReadCoreAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        await VerifyOwnerAsync(session, token);
        return ReadLocked(session, owner, writer) ?? throw new InvalidDataException("The metric loss writer is unavailable or has changed.");
    }

    // The heartbeat already holds the identity lease. Never create or mutate a
    // ledger while producing a snapshot or reacquire that same lease.
    internal static MetricLossSnapshot? ReadLocked(IdentityVault.Session session, AgentSettingsOwner owner, Guid? expectedWriter = null)
    {
        if (!File.Exists(Path.Combine(session.DirectoryPath, "metric-loss.db"))) return null;
        using var connection = Open(session.DirectoryPath, owner, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ledger_id, started_ticks, batches, records, unknown, writer FROM loss_state WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !Guid.TryParseExact(reader.GetString(0), "D", out var id) || id.Version != 7)
            throw new InvalidDataException("Metric loss state is invalid.");
        if (expectedWriter is { } writer && (reader.IsDBNull(5) || !string.Equals(reader.GetString(5), writer.ToString("D"), StringComparison.Ordinal)))
            return null;
        return new(id, new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4) != 0);
    }

    /// <summary>Record a stable, consecutive abandonment event once; retrying the latest event is idempotent.</summary>
    public Task RecordAsync(long sequence, long batches, long records, CancellationToken token) =>
        ExecuteAsync(async ct => { await RecordCoreAsync(sequence, batches, records, ct); return true; }, token);

    private async Task RecordCoreAsync(long sequence, long batches, long records, CancellationToken token)
    {
        if (completed) throw new InvalidOperationException("The metric loss writer is complete.");
        if (sequence < 1 || batches < 1 || records < batches) throw new ArgumentOutOfRangeException(nameof(sequence));
        await using var session = await vault.AcquireForTransportAsync(token);
        await VerifyOwnerAsync(session, token);
        using var connection = Open(session.DirectoryPath, owner);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT writer, sequence, last_batches, last_records, batches, records FROM loss_state WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || !string.Equals(reader.GetString(0), writer.ToString("D"), StringComparison.Ordinal))
            throw new InvalidOperationException("The metric loss writer has changed.");
        var prior = reader.GetInt64(1);
        var lastBatches = reader.GetInt64(2);
        var lastRecords = reader.GetInt64(3);
        var totalBatches = reader.GetInt64(4);
        var totalRecords = reader.GetInt64(5);
        reader.Close();
        if (sequence == prior && batches == lastBatches && records == lastRecords) return;
        if (prior == long.MaxValue || sequence != prior + 1) throw new InvalidOperationException("Metric loss event sequence is invalid.");
        var nextBatches = checked(totalBatches + batches);
        var nextRecords = checked(totalRecords + records);
        command.CommandText = "UPDATE loss_state SET sequence = $sequence, last_batches = $lastBatches, last_records = $lastRecords, batches = $batches, records = $records WHERE singleton = 1 AND writer = $writer";
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$lastBatches", batches);
        command.Parameters.AddWithValue("$lastRecords", records);
        command.Parameters.AddWithValue("$batches", nextBatches);
        command.Parameters.AddWithValue("$records", nextRecords);
        command.Parameters.AddWithValue("$writer", writer.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The metric loss writer has changed.");
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    /// <summary>Record that completeness cannot be established without asserting an exact loss.</summary>
    public Task MarkIncompleteAsync(CancellationToken token) => ExecuteAsync(async ct =>
    {
        if (completed) throw new InvalidOperationException("The metric loss writer is complete.");
        await using var session = await vault.AcquireForTransportAsync(ct);
        await VerifyOwnerAsync(session, ct);
        using var connection = Open(session.DirectoryPath, owner);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE loss_state SET unknown = 1 WHERE singleton = 1 AND writer = $writer";
        command.Parameters.AddWithValue("$writer", writer.ToString("D"));
        ct.ThrowIfCancellationRequested();
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The metric loss writer has changed.");
        return true;
    }, token);

    /// <summary>Complete only when all known pending observations have been settled.</summary>
    public Task CompleteAsync(CancellationToken token) => ExecuteAsync(async ct =>
    {
        if (completed) throw new InvalidOperationException("The metric loss writer is complete.");
        await CompleteCoreAsync(ct);
        completed = true;
        return true;
    }, token);

    private async Task CompleteCoreAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        await VerifyOwnerAsync(session, token);
        using var connection = Open(session.DirectoryPath, owner);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE loss_state SET writer = NULL WHERE singleton = 1 AND writer = $writer";
        command.Parameters.AddWithValue("$writer", writer.ToString("D"));
        token.ThrowIfCancellationRequested();
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The metric loss writer has changed.");
    }

    private async Task VerifyOwnerAsync(IdentityVault.Session session, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (identity?.AgentId != owner.AgentId || !string.Equals(identity.Server, owner.Server, StringComparison.Ordinal) ||
            !string.Equals(owner.StateDirectory, vault.StateDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The metric loss ledger owner has changed.");
    }

    private static SqliteConnection Open(string directory, AgentSettingsOwner owner, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "metric-loss.db"), Pooling = false, DefaultTimeout = 5,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = (long)command.ExecuteScalar()!;
            if (version is not (0 or 1) || readOnly && version != 1) throw new InvalidDataException("Unsupported metric loss ledger format.");
            if (!readOnly)
            {
                command.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL";
                command.ExecuteNonQuery();
            }
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE loss_state (singleton INTEGER PRIMARY KEY CHECK(singleton = 1), agent_id TEXT NOT NULL, server TEXT NOT NULL, ledger_id TEXT NOT NULL, started_ticks INTEGER NOT NULL, batches INTEGER NOT NULL, records INTEGER NOT NULL, unknown INTEGER NOT NULL, writer TEXT NULL, sequence INTEGER NOT NULL, last_batches INTEGER NOT NULL, last_records INTEGER NOT NULL); INSERT INTO loss_state VALUES (1, $owner, $server, $ledger, $started, 0, 0, 1, NULL, 0, 0, 0); PRAGMA user_version = 1";
                command.Parameters.AddWithValue("$owner", owner.AgentId.ToString("D"));
                command.Parameters.AddWithValue("$server", owner.Server);
                command.Parameters.AddWithValue("$ledger", Guid.CreateVersion7().ToString("D"));
                command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.UtcTicks);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            command.Transaction = null;
            command.Parameters.Clear();
            command.CommandText = """
                SELECT agent_id, server, ledger_id, started_ticks, batches, records, unknown, writer, sequence, last_batches, last_records,
                    typeof(agent_id) = 'text' AND typeof(server) = 'text' AND typeof(ledger_id) = 'text'
                    AND typeof(started_ticks) = 'integer' AND typeof(batches) = 'integer' AND typeof(records) = 'integer'
                    AND typeof(unknown) = 'integer' AND typeof(writer) IN ('text', 'null')
                    AND typeof(sequence) = 'integer' AND typeof(last_batches) = 'integer' AND typeof(last_records) = 'integer'
                FROM loss_state WHERE singleton = 1
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("Metric loss state is missing.");
            if (reader.GetInt64(11) != 1) throw new InvalidDataException("Metric loss state has invalid storage types.");
            if (!string.Equals(reader.GetString(0), owner.AgentId.ToString("D"), StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(1), owner.Server, StringComparison.Ordinal))
                throw new InvalidOperationException("The metric loss ledger belongs to a different Agent identity.");
            if (!Guid.TryParseExact(reader.GetString(2), "D", out var ledgerId) || ledgerId.Version != 7 ||
                reader.GetInt64(3) < 1 || reader.GetInt64(3) > DateTimeOffset.MaxValue.Ticks ||
                reader.GetInt64(4) < 0 || reader.GetInt64(5) < reader.GetInt64(4) ||
                reader.GetInt64(6) is not (0 or 1) ||
                (!reader.IsDBNull(7) && (!Guid.TryParseExact(reader.GetString(7), "D", out var currentWriter) || currentWriter.Version != 7)) ||
                reader.GetInt64(8) < 0 || reader.GetInt64(9) < 0 || reader.GetInt64(10) < reader.GetInt64(9) ||
                reader.GetInt64(9) > reader.GetInt64(4) || reader.GetInt64(10) > reader.GetInt64(5) ||
                (reader.GetInt64(8) == 0 && (reader.GetInt64(9) != 0 || reader.GetInt64(10) != 0)) ||
                (reader.GetInt64(8) > 0 && reader.GetInt64(9) == 0))
                throw new InvalidDataException("Metric loss state is invalid.");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ownership.IsHeldFor(vault)) throw new InvalidOperationException("Metric producer ownership is not held.");
            return await operation(token);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (ownsLease) await ownership.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}

public sealed record MetricLossSnapshot(Guid LedgerId, DateTimeOffset StartedAt, long Batches, long Records, bool CompletenessUnknown);
