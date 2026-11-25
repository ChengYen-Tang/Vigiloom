using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Operations;

internal sealed class LineAggregationWorker(IServiceScopeFactory scopes, ILogger<LineAggregationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LineDeliveryQueue>().EnqueueNewAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "LINE aggregation failed; retrying."); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
