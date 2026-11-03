using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Worker.Hosting;

internal enum Scanner { Ping, Speed }

/// <summary>Collects scanner state in memory without delaying monitoring work.</summary>
internal sealed class WorkerDiagnostics(TimeProvider clock)
{
    private readonly Lock gate = new();
    private ScannerObservation ping = new(null, null, 0);
    private ScannerObservation speed = new(null, null, 0);

    public void Success(Scanner scanner) => Record(scanner, ScannerFailureKind.None);
    public void Failure(Scanner scanner, Exception exception) => Record(scanner, exception switch
    {
        Microsoft.Data.SqlClient.SqlException => ScannerFailureKind.Database,
        TimeoutException or OperationCanceledException => ScannerFailureKind.Timeout,
        _ => ScannerFailureKind.Other
    });

    public WorkerObservation Snapshot()
    {
        lock (gate) return new(ping, speed);
    }

    private void Record(Scanner scanner, ScannerFailureKind kind)
    {
        lock (gate)
        {
            var previous = scanner == Scanner.Ping ? ping : speed;
            var now = clock.GetUtcNow();
            var next = kind == ScannerFailureKind.None
                ? previous with { LastSuccess = now, ConsecutiveFailures = 0 }
                : previous with { LastFailure = now, FailureKind = kind, ConsecutiveFailures = previous.ConsecutiveFailures == int.MaxValue ? int.MaxValue : previous.ConsecutiveFailures + 1 };
            if (scanner == Scanner.Ping) ping = next; else speed = next;
        }
    }
}

/// <summary>Renew a diagnostic lease independently from potentially long-running scans.</summary>
internal sealed class WorkerDiagnosticsPublisher(IServiceScopeFactory scopes, WorkerDiagnostics observations, ILogger<WorkerDiagnosticsPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        int? slot = null;
        var boot = Guid.Empty;
        do
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<WorkerDiagnosticsStore>();
                if (slot is not null && !await store.UpdateAsync(slot.Value, boot, observations.Snapshot(), timeout.Token))
                    slot = null;
                if (slot is null)
                {
                    boot = Guid.CreateVersion7();
                    slot = await store.ClaimAsync(boot, timeout.Token);
                    if (slot is null) logger.LogWarning("Worker diagnostic capacity is full; monitoring continues without a diagnostic slot.");
                    else if (!await store.UpdateAsync(slot.Value, boot, observations.Snapshot(), timeout.Token)) slot = null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Could not update worker diagnostics: {ExceptionType}", exception.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
