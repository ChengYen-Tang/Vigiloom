using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Temporalio.Extensions.Hosting;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Connects a service to Temporal and starts its scoped work consumer.</summary>
public static class TemporalRegistration
{
    public static IServiceCollection AddVigiloomTemporalClient(this IServiceCollection services, string address, string temporalNamespace)
    {
        services.AddTemporalClient(address, temporalNamespace);
        services.AddScoped<ServiceWorkClient>();
        services.TryAddScoped<OutboxWriter>();
        services.AddScoped<TemporalWorkWriter>();
        services.AddScoped<TemporalAuditWriter>();
        services.AddScoped<IOutboxHandler, TemporalOutboxHandler>();
        return services;
    }

    public static IServiceCollection AddVigiloomTemporalWorker<THandler>(this IServiceCollection services, string taskQueue)
        where THandler : class, IServiceWorkHandler
    {
        services.AddScoped<IServiceWorkHandler, THandler>();
        services.AddHostedTemporalWorker(taskQueue)
            .AddScopedActivities<ServiceActivities>()
            .AddWorkflow<ServiceWorkflow>();
        return services;
    }
}
