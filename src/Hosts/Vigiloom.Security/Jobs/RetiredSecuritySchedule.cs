using Hangfire;

namespace Vigiloom.Security.Jobs;

internal sealed class RetiredSecuritySchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        string[] ids = ["vigiloom-inventory-expiry", "vigiloom-intelligence-refresh", "vigiloom-debian-refresh",
            "vigiloom-ubuntu-osv-refresh", "vigiloom-msrc-cvrf-refresh", "vigiloom-first-epss-refresh",
            "vigiloom-linux-reassessment", "vigiloom-windows-reassessment", "vigiloom-advisory-refresh",
            "vigiloom-sigma-refresh", "vigiloom-signature-refresh", "vigiloom-baseline-refresh", "vigiloom-integrity-expiry"];
        foreach (var id in ids) jobs.RemoveIfExists(id);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
