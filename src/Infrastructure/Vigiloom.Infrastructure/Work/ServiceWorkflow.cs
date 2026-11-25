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
        new() { StartToCloseTimeout = TimeSpan.FromMinutes(5) });
}

/// <summary>Delegates work to the destination service without storing its dependencies in the workflow.</summary>
public sealed class ServiceActivities(IServiceWorkHandler handler)
{
    [Activity]
    public Task<string?> ExecuteAsync(ServiceWork work) =>
        handler.ExecuteAsync(work, ActivityExecutionContext.Current.CancellationToken);
}
