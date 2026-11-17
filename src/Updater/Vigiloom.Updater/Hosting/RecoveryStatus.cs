using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Hosting;

/// <summary>Reads the local journal during service restoration, without changing it.</summary>
internal enum RecoveryStatus { Ready = 0, Pending = 1, Failed = 2 }

internal static class RecoveryStatusReader
{
    public static async Task<RecoveryStatus> ReadAsync(string directory, CancellationToken token)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return RecoveryStatus.Failed;
            var fullPath = Path.GetFullPath(directory);
            if (string.Equals(fullPath, Path.GetPathRoot(fullPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return RecoveryStatus.Failed;
            var journal = Path.Combine(fullPath, "update.db");
            try { _ = File.GetAttributes(journal); }
            catch (FileNotFoundException) { return RecoveryStatus.Ready; }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = journal, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT State FROM UpdateJournal WHERE Id = 1";
            var value = await command.ExecuteScalarAsync(token);
            if (value is null) return RecoveryStatus.Ready;
            if (value is not string json) return RecoveryStatus.Failed;
            var state = JsonSerializer.Deserialize<UpdateState>(json);
            if (state is null || state.Id.Version != 7 || state.Steps is not { Count: > 0 } ||
                string.IsNullOrWhiteSpace(state.PreviousDirectory) || !Path.IsPathFullyQualified(state.PreviousDirectory) ||
                string.IsNullOrWhiteSpace(state.TargetDirectory) || !Path.IsPathFullyQualified(state.TargetDirectory)) return RecoveryStatus.Failed;
            return state.Phase switch
            {
                UpdatePhase.Succeeded or UpdatePhase.RolledBack => RecoveryStatus.Ready,
                UpdatePhase.Prepared or UpdatePhase.Stopping or UpdatePhase.Switching or UpdatePhase.Starting or
                    UpdatePhase.Verifying or UpdatePhase.RollingBack => RecoveryStatus.Pending,
                _ => RecoveryStatus.Failed
            };
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            return RecoveryStatus.Pending;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or SqliteException or JsonException)
        {
            return RecoveryStatus.Failed;
        }
    }
}
