using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Worker.Hosting;

public sealed class AgentInboxRetentionJob(AgentInboxCleanup cleanup, ILogger<AgentInboxRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(CancellationToken token)
    {
        var count = await cleanup.RunAsync(token);
        logger.LogInformation("Agent inbox retention removed {Count} expired receipts", count);
    }
}

internal sealed class AgentInboxRetentionSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-agent-inbox-retention", Job.FromExpression<AgentInboxRetentionJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
