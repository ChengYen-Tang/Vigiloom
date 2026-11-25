using Temporalio.Client;
using Temporalio.Api.Enums.V1;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Starts and manages a destination service's durable workflow.</summary>
public sealed class ServiceWorkClient(ITemporalClient client)
{
    public Task<WorkflowHandle<ServiceWorkflow, string?>> StartAsync(string workflowId, string taskQueue, ServiceWork work, CancellationToken cancellationToken = default) =>
        client.StartWorkflowAsync((ServiceWorkflow workflow) => workflow.RunAsync(work),
            new() { Id = workflowId, TaskQueue = taskQueue, IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                Rpc = new() { CancellationToken = cancellationToken } });

    public WorkflowHandle<ServiceWorkflow, string?> Get(string workflowId) =>
        client.GetWorkflowHandle<ServiceWorkflow, string?>(workflowId);
}
