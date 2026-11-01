using Hangfire;
using Hangfire.Common;
using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Worker.Hosting;

public sealed class AutomaticSpeedJob(SpeedQueue queue)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token) => await queue.RequestAsync(ResourceIds.Create(), null, true, token);
}
internal sealed class SpeedWorker(IServiceScopeFactory scopes, ILogger<SpeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SpeedRunner>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Speed test scan failed: {ExceptionType}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
internal sealed class SpeedSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-speed-test", Job.FromExpression<AutomaticSpeedJob>(job => job.RunAsync(CancellationToken.None)),
            "* * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
