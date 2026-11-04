using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Worker.Hosting;

internal sealed class HealthWorker(HealthRunner runner, ILogger<HealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try { await runner.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Health scan failed: {ExceptionType}", exception.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Refreshes network policy even while a scan is waiting on remote endpoints.</summary>
internal sealed class HealthPolicyWorker(HealthRunner runner, ILogger<HealthPolicyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try { await runner.RefreshNetworkPolicyAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Health network policy refresh failed: {ExceptionType}", exception.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

internal sealed class HealthExpiryWorker(HealthRunner runner, ILogger<HealthExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await runner.ExpireStaleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Health expiry scan failed: {ExceptionType}", exception.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
