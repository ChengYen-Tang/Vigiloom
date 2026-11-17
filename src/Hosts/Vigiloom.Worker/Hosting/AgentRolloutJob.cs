using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Worker.Hosting;

public sealed class AgentRolloutJob(AgentRollouts rollouts)
{
    public Task RunAsync(CancellationToken token) => rollouts.AdvanceAsync(token);
}

internal sealed class AgentRolloutSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.AddOrUpdate("vigiloom-agent-rollouts", Job.FromExpression<AgentRolloutJob>(job => job.RunAsync(CancellationToken.None)),
            Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
