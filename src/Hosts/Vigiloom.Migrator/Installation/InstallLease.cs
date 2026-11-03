using Microsoft.Data.SqlClient;

namespace Vigiloom.Migrator.Installation;

/// <summary>Serializes deployment and preserves an interrupted fresh-install intent.</summary>
internal sealed class InstallLease(SqlConnection connection) : IAsyncDisposable
{
    internal int SessionId => connection.ServerProcessId;
    public static async Task<InstallLease> AcquireAsync(string connectionString)
    {
        var options = new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectRetryCount = 0 };
        var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.Migrator', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 30000; SELECT @r";
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) < 0) throw new InvalidOperationException("Could not obtain the deployment lock.");
            return new InstallLease(connection);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task VerifyAsync()
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT APPLOCK_MODE('public', 'Vigiloom.Migrator', 'Session')";
        if (!string.Equals(await command.ExecuteScalarAsync() as string, "Exclusive", StringComparison.Ordinal))
            throw new InvalidOperationException("The deployment lock was lost.");
    }

    public async Task<bool> PrepareAsync(bool fresh)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.VigiloomInstallIntent', N'U') IS NULL THEN 0 ELSE 1 END";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        if (!exists && fresh)
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 0) throw new InvalidOperationException("Fresh installation requires an empty target database.");
            command.CommandText = "CREATE TABLE dbo.VigiloomInstallIntent (Id uniqueidentifier NOT NULL PRIMARY KEY, FormatVersion int NOT NULL, CreatedAt datetimeoffset NOT NULL, Completed bit NOT NULL); INSERT INTO dbo.VigiloomInstallIntent VALUES (@id, 1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 0)";
            command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            await command.ExecuteNonQueryAsync();
            exists = true;
        }
        var completed = false;
        if (exists)
        {
            command.CommandText = "SELECT FormatVersion, Completed, Id, CreatedAt FROM dbo.VigiloomInstallIntent";
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.GetInt32(0) != 1) throw new InvalidOperationException("Invalid installation intent.");
            completed = reader.GetBoolean(1);
            if (reader.GetGuid(2).Version != 7 || reader.GetFieldValue<DateTimeOffset>(3).Offset != TimeSpan.Zero)
                throw new InvalidOperationException("Invalid installation intent metadata.");
            if (await reader.ReadAsync()) throw new InvalidOperationException("Multiple installation intents are invalid.");
            if (!fresh && !completed) throw new InvalidOperationException("A fresh installation is incomplete; rerun fresh-install to resume.");
        }
        await transaction.CommitAsync();
        return fresh && completed;
    }

    public async Task CompleteFreshAsync()
    {
        await VerifyAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF APPLOCK_MODE('public', 'Vigiloom.Migrator', 'Session') <> 'Exclusive'
                THROW 51000, 'The deployment lock was lost.', 1;
            IF (SELECT COUNT(*) FROM dbo.VigiloomInstallIntent WITH (UPDLOCK, HOLDLOCK) WHERE FormatVersion = 1 AND Completed = 0) <> 1
                THROW 51000, 'Invalid pending installation intent.', 1;
            IF EXISTS (SELECT 1 FROM [agent].[Agents] WITH (UPDLOCK, HOLDLOCK))
                THROW 51000, 'Cannot initialize a fresh policy after an Agent has registered.', 1;
            DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
            UPDATE [agent].[ReplayPolicies] SET Phase = 2, FreshInstallation = 1, ActivatedAt = @now, EnforcedAt = @now
                WHERE Scope = 1 AND Phase = 0;
            IF @@ROWCOUNT <> 1 THROW 51000, 'Replay policy was already initialized.', 1;
            UPDATE dbo.VigiloomInstallIntent SET Completed = 1 WHERE FormatVersion = 1 AND Completed = 0;
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
