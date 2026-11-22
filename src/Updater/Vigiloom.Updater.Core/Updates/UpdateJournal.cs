using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Updates;

public enum UpdatePhase { Prepared, Stopping, Switching, Starting, Verifying, RollingBack, Succeeded, RolledBack, RollbackFailed, Failed }

public sealed record UpdateStep(UpdatePhase Phase, DateTimeOffset At, string? Error, Guid? MessageId = null);

/// <summary>Local recovery information for the single update handled by an Updater service.</summary>
public sealed record UpdateState(Guid Id, string PreviousDirectory, string TargetDirectory, UpdatePhase Phase,
    DateTimeOffset UpdatedAt, IReadOnlyList<UpdateStep> Steps, int RollbackAttempts = 0, string? Error = null, bool RetryAllowed = false,
    UpdateProvenance? Provenance = null, IReadOnlyList<string>? RetainedFailedDirectories = null,
    string? PendingUpdaterDirectory = null, string? PreviousAgentVersion = null);

/// <summary>Non-secret target identity retained across local recovery.</summary>
public sealed record UpdateProvenance(Guid AgentId, Guid PackageId, string ManifestSha256, string? PackageVersion = null)
{
    public bool IsValid() => AgentId.Version == 7 && PackageId.Version == 7 && ManifestSha256 is { Length: 64 } &&
        ManifestSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public enum UpdateFailure { Interrupted = 1, AuthorizationUnavailable = 2, Other = 3 }

/// <summary>An immutable terminal observation awaiting central acknowledgement.</summary>
public sealed record UpdateOutcome(Guid MessageId, Guid UpdateId, UpdateProvenance Provenance, int Sequence,
    UpdatePhase Phase, DateTimeOffset OccurredAt, bool RetryAllowed, UpdateFailure? Failure)
{
    public AgentUpdateResult ToReport() => new(MessageId, UpdateId, Provenance.PackageId, Provenance.ManifestSha256, Sequence,
        Phase switch
        {
            UpdatePhase.Succeeded => AgentUpdateResultKind.Succeeded,
            UpdatePhase.RolledBack => AgentUpdateResultKind.RolledBack,
            UpdatePhase.RollbackFailed => AgentUpdateResultKind.RollbackFailed,
            UpdatePhase.Failed => AgentUpdateResultKind.Failed,
            _ => throw new InvalidDataException("The update outcome is not terminal.")
        }, OccurredAt, RetryAllowed, Failure switch
        {
            null => null,
            UpdateFailure.Interrupted => AgentUpdateFailure.Interrupted,
            UpdateFailure.AuthorizationUnavailable => AgentUpdateFailure.AuthorizationUnavailable,
            UpdateFailure.Other => AgentUpdateFailure.Other,
            _ => throw new InvalidDataException("The update failure category is invalid.")
        });
}

public interface IUpdateJournal
{
    Task<UpdateState?> ReadAsync(CancellationToken token);
    Task WriteAsync(UpdateState state, CancellationToken token);
}

public sealed class UpdateJournal(string stateDirectory) : IUpdateJournal
{
    private readonly string directory = Path.GetFullPath(stateDirectory);
    private string PathName => Path.Combine(directory, "update.db");

    public async Task<UpdateState?> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(PathName)) return null;
        await using var connection = Open(SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM UpdateJournal WHERE Id = 1";
        var value = await command.ExecuteScalarAsync(token);
        return value is null ? null : JsonSerializer.Deserialize<UpdateState>((string)value)
            ?? throw new InvalidDataException("The update journal is empty.");
    }

    /// <summary>Persists the intended next operation before it can change the active version.</summary>
    public async Task WriteAsync(UpdateState state, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Provenance is { } target && (!target.IsValid() || state.Id.Version != 7))
            throw new ArgumentException("The update provenance is invalid.", nameof(state));
        Directory.CreateDirectory(directory);
        await using var connection = Open(SqliteOpenMode.ReadWriteCreate);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO UpdateJournal (Id, State) VALUES (1, $state) ON CONFLICT(Id) DO UPDATE SET State = excluded.State";
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state));
        await command.ExecuteNonQueryAsync(token);
        if (state.Provenance is { } provenance && state.Phase is UpdatePhase.Succeeded or UpdatePhase.RolledBack or UpdatePhase.RollbackFailed or UpdatePhase.Failed)
        {
            var outcome = new UpdateOutcome(Guid.CreateVersion7(), state.Id, provenance, state.Steps.Count, state.Phase,
                state.UpdatedAt.ToUniversalTime(), state.RetryAllowed, Failure(state.Error));
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO UpdateOutbox (MessageId, AgentId, UpdateId, Sequence, Payload) VALUES ($message, $agent, $update, $sequence, $payload) ON CONFLICT(UpdateId, Sequence) DO NOTHING";
            command.Parameters.AddWithValue("$message", outcome.MessageId.ToString("D"));
            command.Parameters.AddWithValue("$agent", provenance.AgentId.ToString("D"));
            command.Parameters.AddWithValue("$update", state.Id.ToString("D"));
            command.Parameters.AddWithValue("$sequence", outcome.Sequence);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(outcome));
            if (await command.ExecuteNonQueryAsync(token) == 0)
            {
                command.CommandText = "SELECT Payload FROM UpdateOutbox WHERE UpdateId = $update AND Sequence = $sequence";
                var stored = await command.ExecuteScalarAsync(token) as string;
                var existing = stored is null ? null : JsonSerializer.Deserialize<UpdateOutcome>(stored);
                if (existing is null || existing with { MessageId = outcome.MessageId } != outcome)
                    throw new InvalidDataException("An update outcome with different contents already exists.");
            }
        }
        await transaction.CommitAsync(token);
    }

    /// <summary>Reads pending observations for the current authenticated Agent only.</summary>
    public async Task<IReadOnlyList<UpdateOutcome>> ReadPendingAsync(Guid agentId, int limit, CancellationToken token)
    {
        if (agentId.Version != 7 || limit is < 1 or > 100) throw new ArgumentException("A valid Agent and bounded limit are required.");
        if (!File.Exists(PathName)) return [];
        await using var connection = Open(SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM UpdateOutbox WHERE AgentId = $agent AND Acknowledged = 0 ORDER BY rowid LIMIT $limit";
        command.Parameters.AddWithValue("$agent", agentId.ToString("D"));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<UpdateOutcome>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(JsonSerializer.Deserialize<UpdateOutcome>(reader.GetString(0)) ?? throw new InvalidDataException("An update outcome is invalid."));
        return result;
    }

    /// <summary>Marks only a confirmed observation for this Agent; unconfirmed observations remain durable.</summary>
    public async Task AcknowledgeAsync(Guid agentId, Guid messageId, CancellationToken token)
    {
        if (agentId.Version != 7 || messageId.Version != 7) throw new ArgumentException("Valid identifiers are required.");
        if (!File.Exists(PathName)) return;
        await using var connection = Open(SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE UpdateOutbox SET Acknowledged = 1 WHERE AgentId = $agent AND MessageId = $message";
        command.Parameters.AddWithValue("$agent", agentId.ToString("D"));
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        await command.ExecuteNonQueryAsync(token);
    }

    private static UpdateFailure? Failure(string? error) => error switch
    {
        null => null,
        "Interrupted" or "InterruptedBeforeStop" => UpdateFailure.Interrupted,
        "UpdateAuthorizationUnavailableException" => UpdateFailure.AuthorizationUnavailable,
        _ => UpdateFailure.Other
    };

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathName, Mode = mode, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA synchronous = EXTRA; CREATE TABLE IF NOT EXISTS UpdateJournal (Id INTEGER PRIMARY KEY CHECK (Id = 1), State TEXT NOT NULL); CREATE TABLE IF NOT EXISTS UpdateOutbox (MessageId TEXT PRIMARY KEY, AgentId TEXT NOT NULL, UpdateId TEXT NOT NULL, Sequence INTEGER NOT NULL, Payload TEXT NOT NULL, Acknowledged INTEGER NOT NULL DEFAULT 0, UNIQUE (UpdateId, Sequence)); CREATE INDEX IF NOT EXISTS IX_UpdateOutbox_AgentPending ON UpdateOutbox (AgentId, Acknowledged);";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
