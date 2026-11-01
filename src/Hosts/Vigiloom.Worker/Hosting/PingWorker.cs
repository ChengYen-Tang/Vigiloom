using Vigiloom.Modules.NetworkMonitoring.Application;

namespace Vigiloom.Worker.Hosting;

internal sealed class PingWorker(IServiceScopeFactory scopes, ILogger<PingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PingRunner>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Ping scan failed: {ExceptionType}", exception.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
