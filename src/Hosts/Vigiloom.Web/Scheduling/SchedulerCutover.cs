using Hangfire;

namespace Vigiloom.Web.Scheduling;

internal sealed class SchedulerCutover(IServiceScopeFactory scopes, JobStorage storage, ILogger<SchedulerCutover> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BackgroundJobServer? scheduler = null;
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var ready = await scope.ServiceProvider.GetRequiredService<LegacyWorkerRetirement>().RunAsync(stoppingToken);
                    if (ready && scheduler is null)
                        scheduler = new BackgroundJobServer(new BackgroundJobServerOptions { Queues = ["scheduler"] }, storage);
                    else if (!ready && scheduler is not null)
                    {
                        scheduler.Dispose();
                        scheduler = null;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    scheduler?.Dispose();
                    scheduler = null;
                    logger.LogWarning("Could not reconcile legacy schedules: {ErrorType}", error.GetType().Name);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { scheduler?.Dispose(); }
    }
}
