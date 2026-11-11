using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;

namespace Vigiloom.Testing.SqlServer;

/// <summary>Exclusive reuse of a run-owned database with a caller-provided reset contract.</summary>
public sealed class ReusableSqlTestDatabase : IAsyncDisposable
{
    private readonly SemaphoreSlim Gate = new(1, 1);
    private readonly List<SqlTestDatabase> PendingCleanup = [];
    private SqlTestDatabase? database;
    private Func<SqlConnection, CancellationToken, Task>? reset;
    private bool disposed;

    public ReusableSqlTestDatabase(Func<SqlTestDatabase, CancellationToken, Task<Func<SqlConnection, CancellationToken, Task>>> initialize)
        => this.initialize = initialize;

    private readonly Func<SqlTestDatabase, CancellationToken, Task<Func<SqlConnection, CancellationToken, Task>>> initialize;

    public async Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token,
        Func<SqlConnection, Task>? resetOverride = null, Func<SqlTestDatabase, ValueTask>? disposeOverride = null)
    {
        await Gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (database is null)
            {
                database = await SqlTestDatabase.CreateAsync(TestDatabaseServer.FromEnvironment(), async (created, ct) =>
                    reset = await initialize(created, ct), token);
            }

            Exception? failure = null;
            try { await action(database, token); }
            catch (Exception error) { failure = error; }
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(cleanup.Token);
                if (resetOverride is null) await reset!(connection, cleanup.Token);
                else await resetOverride(connection);
            }
            catch (Exception cleanupFailure)
            {
                var discarded = database;
                database = null;
                reset = null;
                try { if (disposeOverride is null) await discarded.DisposeAsync(); else await disposeOverride(discarded); }
                catch (Exception disposeFailure)
                {
                    PendingCleanup.Add(discarded);
                    if (failure is not null) throw new AggregateException("Test, database reset and disposal failed.", failure, cleanupFailure, disposeFailure);
                    throw new AggregateException("Database reset and disposal failed.", cleanupFailure, disposeFailure);
                }
                if (failure is not null) throw new AggregateException("Test and database reset failed.", failure, cleanupFailure);
                throw;
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            disposed = true;
            if (database is not null) { PendingCleanup.Add(database); database = null; reset = null; }
            List<Exception> failures = [];
            foreach (var item in PendingCleanup)
                try { await item.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Some test databases could not be cleaned up.", failures);
            PendingCleanup.Clear();
        }
        finally { Gate.Release(); }
    }

}
