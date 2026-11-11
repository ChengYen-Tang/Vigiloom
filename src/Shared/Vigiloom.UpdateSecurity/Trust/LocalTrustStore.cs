using Microsoft.Data.Sqlite;

namespace Vigiloom.UpdateSecurity;

/// <summary>Persists a node's authenticated TUF state in an exclusively locked local SQLite database.</summary>
public sealed class LocalTrustStore(string directory)
{
    private readonly string directory = Path.GetFullPath(directory);

    /// <summary>Explicitly creates a new store from a separately authenticated root.</summary>
    public async Task ProvisionAsync(byte[] trustedRoot, CancellationToken cancellationToken = default)
    {
        var snapshot = TrustMetadataSnapshot.Provision(trustedRoot);
        Directory.CreateDirectory(directory);
        await using var guard = await ExclusiveLocalLock.AcquireAsync(Path.Combine(directory, "trust.lock"), cancellationToken);
        var path = Path.Combine(directory, "trust.sqlite");
        // An existing (including incomplete) store must never be silently reinitialized.
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        await using var connection = Open(path);
        using var command = connection.CreateCommand();
        var (data, digest) = snapshot.Encode();
        command.CommandText = """
            CREATE TABLE trust (id INTEGER PRIMARY KEY CHECK (id = 1),
                snapshot BLOB NOT NULL CHECK (typeof(snapshot) = 'blob' AND length(snapshot) BETWEEN 1 AND 6291456),
                digest BLOB NOT NULL CHECK (typeof(digest) = 'blob' AND length(digest) = 32));
            INSERT INTO trust (id, snapshot, digest) VALUES (1, $snapshot, $digest);
            """;
        command.Parameters.AddWithValue("$snapshot", data);
        command.Parameters.AddWithValue("$digest", digest);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ITrustMetadataLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var guard = await ExclusiveLocalLock.AcquireAsync(Path.Combine(directory, "trust.lock"), cancellationToken);
        SqliteConnection? connection = null;
        try
        {
            connection = Open(Path.Combine(directory, "trust.sqlite"));
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CASE WHEN typeof(snapshot) = 'blob' AND length(snapshot) BETWEEN 1 AND $max THEN snapshot END,
                       CASE WHEN typeof(digest) = 'blob' AND length(digest) = 32 THEN digest END
                FROM trust WHERE id = 1
                """;
            command.Parameters.AddWithValue("$max", TrustMetadataSnapshot.MaximumSnapshotBytes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("Local update trust state is missing.");
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) throw new InvalidDataException("Local update trust state has an invalid size or type.");
            var data = (byte[])reader.GetValue(0);
            var digest = (byte[])reader.GetValue(1);
            var snapshot = TrustMetadataSnapshot.Decode(data, digest);
            return new Lease(guard, connection, digest, snapshot);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            await guard.DisposeAsync();
            throw;
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous = FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private sealed class Lease(FileStream guard, SqliteConnection connection, byte[] digest, TrustMetadataSnapshot snapshot) : ITrustMetadataLease
    {
        private bool disposed;
        public byte[]? LoadMetadata(string role) { ThrowIfLost(); return snapshot.Read(role); }

        public void StoreMetadata(string role, ReadOnlySpan<byte> data)
        {
            ThrowIfLost();
            var next = snapshot.WithMetadata(role, data);
            var (bytes, nextDigest) = next.Encode();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE trust SET snapshot = $snapshot, digest = $next WHERE id = 1 AND digest = $previous";
            command.Parameters.AddWithValue("$snapshot", bytes);
            command.Parameters.AddWithValue("$next", nextDigest);
            command.Parameters.AddWithValue("$previous", digest);
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Local update trust state changed or is missing.");
            snapshot = next;
            digest = nextDigest;
        }

        public Task VerifyHeldAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfLost();
            return Task.CompletedTask;
        }

        private void ThrowIfLost()
        {
            ObjectDisposedException.ThrowIf(disposed || guard.SafeFileHandle.IsClosed, this);
            if (connection.State != System.Data.ConnectionState.Open) throw new InvalidOperationException("Local update trust connection was lost.");
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { await connection.DisposeAsync(); }
            finally { await guard.DisposeAsync(); }
        }
    }
}
