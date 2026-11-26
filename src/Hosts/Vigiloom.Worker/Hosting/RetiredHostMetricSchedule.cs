using Hangfire;

namespace Vigiloom.Worker.Hosting;

/// <summary>Removes legacy metric schedules after the host service takes ownership.</summary>
internal sealed class RetiredHostMetricSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.RemoveIfExists("vigiloom-host-metric-rollup");
        jobs.RemoveIfExists("vigiloom-host-metric-retention");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
