using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Agent.Hosting.Logs;

namespace Vigiloom.Agent.Hosting;

/// <summary>Composition root for the Agent host.</summary>
public static class AgentHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Agent");
        builder.Services.AddSystemd();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpClient<IdentityClient>(ConfigureClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
        builder.Services.AddHttpClient<AgentHeartbeatClient>(ConfigureClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
        builder.Services.AddHttpClient<AgentBatchClient>(ConfigureClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
        builder.Services.AddHttpClient<AgentSettingsClient>(ConfigureClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
        builder.Services.AddHttpClient<AgentQueueCapacityClient>(ConfigureClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
        builder.Services.AddHttpClient<AgentMetricSettingsClient>(ConfigureClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
        builder.Services.AddHttpClient<AgentLogSettingsClient>(client => { ConfigureClient(client); client.MaxResponseContentBufferSize = 512 * 1024; }).ConfigurePrimaryHttpMessageHandler(CreateHandler);
        builder.Services.AddSingleton<AgentSettingsRuntime>();
        builder.Services.AddSingleton<AgentQueueCapacityRuntime>();
        builder.Services.AddSingleton<AgentMetricSettingsRuntime>();
        builder.Services.AddSingleton<SamplingBudgetCoordinator>();
        builder.Services.AddSingleton<AgentLogSettingsRuntime>();
        builder.Services.AddSingleton<AgentLogStatusRuntime>();
        builder.Services.AddHostedService<AgentSettingsWorker>();
        builder.Services.AddHostedService<HeartbeatWorker>();
        builder.Services.AddHostedService<BatchWorker>();
        // Stop the producer before the sender during default sequential shutdown.
        builder.Services.AddHostedService<MetricWorker>();
        builder.Services.AddHostedService<LogWorker>();
        return builder;
    }

    private static void ConfigureClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = 64 * 1024;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };
}
