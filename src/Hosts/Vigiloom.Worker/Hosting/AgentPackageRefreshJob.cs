using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Worker.Hosting;

public sealed class AgentPackageRefreshJob(AgentPackageSourceSettings settings, UpdateTrustStore trust)
{
    public Task<bool> RunAsync(CancellationToken token) => settings.RefreshScheduledAsync(trust, token);
}

internal sealed class AgentPackageRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.AddOrUpdate("vigiloom-agent-package-refresh", Job.FromExpression<AgentPackageRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
