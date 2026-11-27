using Hangfire;
using Hangfire.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Infrastructure.Scheduling;

public sealed class OutboxSchedule(IServiceScopeFactory scopes, ILogger<OutboxSchedule> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private Task? reconcile;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Synchronize();
        reconcile = ReconcileAsync(stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        if (reconcile is not null) await reconcile.WaitAsync(cancellationToken);
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { Synchronize(); }
                catch (Exception exception) { logger.LogWarning("Could not synchronize the background schedule: {ExceptionType}", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Synchronize()
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutboxScheduleManager>().EnsureDefault();
        var storage = scope.ServiceProvider.GetRequiredService<JobStorage>();
        using var connection = storage.GetConnection();
        using var gate = connection.AcquireDistributedLock("vigiloom:outbox-queue-migration", TimeSpan.FromSeconds(15));
        var monitoring = storage.GetMonitoringApi();
        using var sql = new SqlConnection(SchedulingRegistration.HangfireConnection(scope.ServiceProvider.GetRequiredService<IConfiguration>()));
        sql.Open();
        var offset = 0;
        while (true)
        {
            var batch = monitoring.EnqueuedJobs("default", offset, 100);
            if (batch.Count == 0) break;
            var ids = batch.Where(item => item.Value.Job?.Type == typeof(OutboxDispatcher) && item.Value.Job.Method.Name == nameof(OutboxDispatcher.DispatchAsync))
                .Select(item => item.Key).ToArray();
            var moved = 0;
            if (ids.Length > 0)
            {
                using var command = sql.CreateCommand();
                command.CommandText = """
                    UPDATE queue SET [Queue] = @target
                    FROM [hangfire].[JobQueue] AS queue
                    INNER JOIN [hangfire].[Job] AS job ON job.[Id] = queue.[JobId]
                    WHERE queue.[Queue] = 'default' AND queue.[FetchedAt] IS NULL AND job.[StateName] = 'Enqueued'
                        AND queue.[JobId] IN (SELECT TRY_CONVERT(bigint, [value]) FROM OPENJSON(@ids));
                    """;
                command.Parameters.AddWithValue("@target", OutboxScheduleManager.Queue);
                command.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(ids));
                moved = command.ExecuteNonQuery();
            }
            offset += batch.Count - moved;
            if (batch.Count < 100) break;
        }
    }

    public void Dispose() => stopping.Dispose();
}
