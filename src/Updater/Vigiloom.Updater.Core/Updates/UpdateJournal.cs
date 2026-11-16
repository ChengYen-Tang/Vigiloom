using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Vigiloom.Updater.Core.Updates;

public enum UpdatePhase { Prepared, Stopping, Switching, Starting, Verifying, RollingBack, Succeeded, RolledBack, RollbackFailed }

public sealed record UpdateStep(UpdatePhase Phase, DateTimeOffset At, string? Error);

/// <summary>Local recovery information for the single update handled by an Updater service.</summary>
public sealed record UpdateState(Guid Id, string PreviousDirectory, string TargetDirectory, UpdatePhase Phase,
    DateTimeOffset UpdatedAt, IReadOnlyList<UpdateStep> Steps, int RollbackAttempts = 0, string? Error = null, bool RetryAllowed = false);

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
        Directory.CreateDirectory(directory);
        await using var connection = Open(SqliteOpenMode.ReadWriteCreate);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO UpdateJournal (Id, State) VALUES (1, $state) ON CONFLICT(Id) DO UPDATE SET State = excluded.State";
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state));
        await command.ExecuteNonQueryAsync(token);
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathName, Mode = mode, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA synchronous = EXTRA; CREATE TABLE IF NOT EXISTS UpdateJournal (Id INTEGER PRIMARY KEY CHECK (Id = 1), State TEXT NOT NULL);";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
