using Hangfire;
using Hangfire.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;

public sealed class WindowsReassessmentJob(WindowsReassessment reassessment)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken token) => reassessment.RunAsync(token);
}
