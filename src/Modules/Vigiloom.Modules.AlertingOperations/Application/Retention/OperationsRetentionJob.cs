using Hangfire;
using Microsoft.Extensions.Logging;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;

namespace Vigiloom.Modules.AlertingOperations.Application.Retention;

/// <summary>Runs Operations-owned history cleanup from its Hangfire queue.</summary>
public sealed class OperationsRetentionJob(AuditRetentionCleanup cleanup, TimelineLogCleanup timeline, ILogger<OperationsRetentionJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(10)]
    public async Task RunAsync(CancellationToken token)
    {
        var removed = await cleanup.RunAsync(token);
        logger.LogInformation("Operations retention removed {Count} records", removed);
        var summaries = await timeline.RunAsync(token);
        logger.LogInformation("Log retention removed {Count} timeline summaries", summaries);
    }
}
