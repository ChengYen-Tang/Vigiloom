using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Worker.Hosting;

public sealed class MetricRollupJob(MetricRollupCoordinator coordinator)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public Task RunAsync(CancellationToken token) => coordinator.RunAsync(token);
}

internal sealed class MetricRollupSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-metric-rollup", Job.FromExpression<MetricRollupJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
