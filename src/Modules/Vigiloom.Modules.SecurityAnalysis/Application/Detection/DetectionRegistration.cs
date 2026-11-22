using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Registers the same host log dispatch contract in Web and Worker.</summary>
public static class DetectionRegistration
{
    public static IServiceCollection AddHostDetection(this IServiceCollection services)
    {
        services.TryAddScoped<IIocSource, IocSource>();
        services.AddScoped<SigmaCatalog>();
        services.AddScoped<SigmaRuleSetLoader>();
        services.AddScoped<SigmaEnabledRules>();
        services.AddScoped<SigmaRuleInventory>();
        services.AddScoped<SigmaDetectionHistory>();
        services.AddScoped<IocDetectionHistory>();
        services.AddScoped<SigmaTesting>();
        services.AddScoped<SigmaRuleManagement>();
        services.AddScoped(provider => new SigmaRuleService(provider.GetRequiredService<SigmaTesting>(), provider.GetRequiredService<SigmaRuleManagement>()));
        services.AddScoped<IHostLogDispatchWriter, HostLogDispatchWriter>();
        services.AddScoped<IOutboxHandler, HostLogDispatchHandler>();
        services.AddScoped<IOutboxHandler, IocHostLogHandler>();
        return services;
    }
}
