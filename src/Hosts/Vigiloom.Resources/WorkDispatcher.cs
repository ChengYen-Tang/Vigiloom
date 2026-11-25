using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Resources;

/// <summary>Dispatches resource transactions to Temporal.</summary>
internal sealed class WorkDispatcher(IServiceScopeFactory scopes, ILogger<WorkDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Resource work dispatch failed; retrying."); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
