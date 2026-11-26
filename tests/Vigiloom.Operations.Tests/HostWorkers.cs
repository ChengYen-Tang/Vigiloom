using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Vigiloom.Operations.Tests;

internal static class HostWorkers
{
    internal static void RemoveTemporal(IServiceCollection services) => Remove(services, descriptor =>
        descriptor.ImplementationFactory?.Method.Name.Contains("AddHostedTemporalWorker", StringComparison.Ordinal) == true);

    internal static void RemoveBackgroundWork(IServiceCollection services)
    {
        RemoveTemporal(services);
        Remove(services, descriptor => descriptor.ImplementationType?.Name is "OperationsRetentionSchedule" or "WorkDispatcher" or "LineAggregationWorker"
            || descriptor.ImplementationFactory?.Method.Name.Contains("AddHangfireServerInner", StringComparison.Ordinal) == true);
    }

    private static void Remove(IServiceCollection services, Func<ServiceDescriptor, bool> predicate)
    {
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) && predicate(item)).ToArray())
            services.Remove(descriptor);
    }
}
