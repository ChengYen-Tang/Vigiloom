using Temporalio.Client;
using Temporalio.Worker;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Registers a service's workflow and activity on its own task queue.</summary>
public static class ServiceWorkWorker
{
    public static TemporalWorker Create(ITemporalClient client, string taskQueue, IServiceWorkHandler handler)
    {
        var activities = new ServiceActivities(handler);
        return new TemporalWorker(client, new TemporalWorkerOptions(taskQueue)
            .AddWorkflow<ServiceWorkflow>()
            .AddActivity(activities.ExecuteAsync));
    }
}
