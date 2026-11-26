using Hangfire;
using Hangfire.Common;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Network;

internal abstract class PollingWorker<T>(IServiceScopeFactory scopes, ILogger logger, TimeSpan interval) : BackgroundService where T : notnull
{
    protected abstract Task RunAsync(T worker, CancellationToken token);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunAsync(scope.ServiceProvider.GetRequiredService<T>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Network worker {Worker} failed; retrying.", typeof(T).Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

internal sealed class PingWorker(IServiceScopeFactory scopes, ILogger<PingWorker> logger) : PollingWorker<PingRunner>(scopes, logger, TimeSpan.FromSeconds(1))
{
    protected override Task RunAsync(PingRunner worker, CancellationToken token) => worker.RunOnceAsync(token);
}
internal sealed class SpeedWorker(IServiceScopeFactory scopes, ILogger<SpeedWorker> logger) : PollingWorker<SpeedRunner>(scopes, logger, TimeSpan.FromSeconds(1))
{
    protected override Task RunAsync(SpeedRunner worker, CancellationToken token) => worker.RunOnceAsync(token);
}
internal sealed class WorkDispatcher(IServiceScopeFactory scopes, ILogger<WorkDispatcher> logger) : PollingWorker<OutboxDispatcher>(scopes, logger, TimeSpan.FromSeconds(2))
{
    protected override Task RunAsync(OutboxDispatcher worker, CancellationToken token) => worker.DispatchAsync(token);
}
internal sealed class SpeedSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-speed-test", Job.FromExpression<AutomaticSpeedJob>(job => job.RunAsync(CancellationToken.None), "network"),
            "* * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
