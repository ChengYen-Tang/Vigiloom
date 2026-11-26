using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Worker.Hosting;

internal sealed class LineDeliveryJob(LineDeliveryQueue queue, IConfiguration configuration)
{
    public Task RunAsync(CancellationToken token) => string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"])
        ? queue.EnqueueNewAsync(token) : Task.CompletedTask;
}

internal sealed class LineDeliverySchedule(IRecurringJobManager jobs, IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"]))
        {
            jobs.RemoveIfExists("vigiloom-line-delivery");
            return Task.CompletedTask;
        }
        jobs.AddOrUpdate("vigiloom-line-delivery", Job.FromExpression<LineDeliveryJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
