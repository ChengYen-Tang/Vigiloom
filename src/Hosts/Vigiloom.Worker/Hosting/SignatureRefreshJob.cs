using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

namespace Vigiloom.Worker.Hosting;

public sealed class SignatureRefreshJob(SignatureRefresh refresh, ILogger<SignatureRefreshJob> logger)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken token)
    {
        var result = await refresh.RunDueAsync(token);
        if (result == SignatureRefreshResult.Failed) throw new InvalidOperationException("Signature-Base source refresh failed; see source status.");
        if (result == SignatureRefreshResult.Conflict) logger.LogWarning("Signature-Base source refresh ended with {Result}", result);
    }
}

internal sealed class SignatureRefreshSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-signature-refresh", Job.FromExpression<SignatureRefreshJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
