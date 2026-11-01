using Vigiloom.Infrastructure.Scheduling;

namespace Vigiloom.Worker.Hosting;

internal sealed class OutboxSchedule(IServiceScopeFactory scopes, ILogger<OutboxSchedule> logger) : IHostedService, IDisposable
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
    }

    public void Dispose() => stopping.Dispose();
}
