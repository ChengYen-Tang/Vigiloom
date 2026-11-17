using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AlertingOperations.Application.Retention;

namespace Vigiloom.Worker.Hosting;

public sealed class AuditRetentionJob(AuditRetentionCleanup cleanup, ILogger<AuditRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public async Task RunAsync(CancellationToken token)
    {
        var removed = await cleanup.RunAsync(token);
        logger.LogInformation("Settings audit retention removed {Count} records", removed);
    }
}

internal sealed class AuditRetentionSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-settings-audit-retention", Job.FromExpression<AuditRetentionJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
