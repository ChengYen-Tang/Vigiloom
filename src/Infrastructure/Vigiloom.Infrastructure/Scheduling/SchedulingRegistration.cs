using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Infrastructure.Scheduling;

public static class SchedulingRegistration
{
    public const string HangfireSchema = "hangfire";

    public static IServiceCollection AddVigiloomScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHangfire((_, settings) => settings
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(Connection(configuration), StorageOptions(prepareSchema: false)));
        services.AddScoped<OutboxWriter>();
        services.AddScoped<DashboardAuditStore>();
        services.AddScoped<SchedulingOverview>();
        services.AddSingleton<SchedulingReadStorage>();
        services.AddScoped<SchedulingHealth>();
        services.AddScoped<SchedulingAuditFeed>();
        services.AddScoped<OutboxScheduleManager>();
        services.AddScoped(provider => new OutboxDispatcher(Connection(configuration), provider.GetServices<IOutboxHandler>(),
            provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
        return services;
    }

    public static string Connection(IConfiguration configuration) => configuration.GetConnectionString("Vigiloom")
        ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required.");

    public static SqlServerStorageOptions StorageOptions(bool prepareSchema) => new()
    {
        SchemaName = HangfireSchema,
        PrepareSchemaIfNecessary = prepareSchema,
        SqlClientFactory = SqlClientFactory.Instance,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true,
        QueuePollInterval = TimeSpan.FromSeconds(15)
    };
}
