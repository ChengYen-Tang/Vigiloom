using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Worker.Hosting;

internal sealed class LineDeliveryJob(LineDeliveryQueue queue)
{
    public Task RunAsync(CancellationToken token) => queue.EnqueueNewAsync(token);
}

internal sealed class LineDeliverySchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.AddOrUpdate("vigiloom-line-delivery", Job.FromExpression<LineDeliveryJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RetiredLineDeliverySchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.RemoveIfExists("vigiloom-line-delivery");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
