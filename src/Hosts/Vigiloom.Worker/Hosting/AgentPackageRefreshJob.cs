using Hangfire;

namespace Vigiloom.Worker.Hosting;

// Remove the recurring job from installations that used the former package source.
internal sealed class AgentPackageRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.RemoveIfExists("vigiloom-agent-package-refresh");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
