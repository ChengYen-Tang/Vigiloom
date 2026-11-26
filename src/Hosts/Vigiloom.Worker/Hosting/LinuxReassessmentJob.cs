using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Worker.Hosting;

public sealed class LinuxReassessmentJob(LinuxReassessment reassessment, IConfiguration configuration)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken token) => SecurityCutover.IsEnabled(configuration) ? Task.CompletedTask : reassessment.RunAsync(token);
}

internal sealed class LinuxReassessmentSchedule(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken token)
    {
        jobs.AddOrUpdate("vigiloom-linux-reassessment", Job.FromExpression<LinuxReassessmentJob>(job => job.RunAsync(CancellationToken.None)),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
