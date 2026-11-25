using Temporalio.Activities;
using Temporalio.Workflows;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Tracks one destination service operation until its activity completes.</summary>
[Workflow]
public sealed class ServiceWorkflow
{
    [WorkflowRun]
    public Task<string?> RunAsync(ServiceWork work) => Workflow.ExecuteActivityAsync(
        (ServiceActivities activity) => activity.ExecuteAsync(work),
        new()
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(5),
            HeartbeatTimeout = TimeSpan.FromSeconds(30),
            CancellationType = ActivityCancellationType.WaitCancellationCompleted
        });
}

/// <summary>Delegates work to the destination service without storing its dependencies in the workflow.</summary>
public sealed class ServiceActivities(IServiceWorkHandler handler)
{
    [Activity]
    public async Task<string?> ExecuteAsync(ServiceWork work)
    {
        var context = ActivityExecutionContext.Current;
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var heartbeat = SendHeartbeatsAsync(context, heartbeatStop.Token);
        try
        {
            return await handler.ExecuteAsync(work, context.CancellationToken);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
        }
    }

    private static async Task SendHeartbeatsAsync(ActivityExecutionContext context, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken)) context.Heartbeat();
    }
}
