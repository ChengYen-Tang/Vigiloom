using Hangfire;
using Hangfire.SqlServer;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Network;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddModuleSqlServer<NetworkDbContext>(services => services.GetRequiredService<IConfiguration>().GetConnectionString("Network")
    ?? throw new InvalidOperationException("ConnectionStrings:Network is required."), "network", pooled: true);
builder.Services.AddModuleSqlServer<SchedulingDbContext>(services => services.GetRequiredService<IConfiguration>().GetConnectionString("Network")
    ?? throw new InvalidOperationException("ConnectionStrings:Network is required."), "scheduling");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddServiceDiscovery();
builder.Services.AddHttpClient<IDevicePingSource, RemoteDevicePingSource>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233", builder.Configuration["Temporal:Namespace"] ?? "default");
if (builder.Configuration.GetValue("Temporal:EnableNetworkWorker", true))
    builder.Services.AddVigiloomTemporalWorker<DeviceChangeHandler>(builder.Configuration["Work:NetworkQueue"] ?? "vigiloom.network.resources");
builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, TemporalAuditWriter>();
builder.Services.AddScoped<IPingTransitionSink, TemporalPingTransitionSink>();
builder.Services.AddScoped<PingSettings>();
builder.Services.AddScoped<PingStore>();
builder.Services.AddScoped<IPingProbe, SystemPingProbe>();
builder.Services.AddScoped<PingRunner>();
builder.Services.AddScoped<SpeedSettings>();
builder.Services.AddScoped<SpeedQueue>();
builder.Services.AddScoped<SpeedRunner>();
builder.Services.AddHttpClient(SpeedtestNetProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(SpeedtestNetProbe.CreatePrimaryHandler);
builder.Services.AddScoped<ISpeedProbe, SpeedtestNetProbe>();
builder.Services.AddScoped(provider => new OutboxDispatcher(provider.GetRequiredService<IConfiguration>().GetConnectionString("Network")
    ?? throw new InvalidOperationException("ConnectionStrings:Network is required."), provider.GetServices<IOutboxHandler>(), provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
builder.Services.AddHostedService<PingWorker>();
builder.Services.AddHostedService<SpeedWorker>();
builder.Services.AddHostedService<WorkDispatcher>();
if (builder.Configuration.GetValue("Hangfire:Enabled", true))
{
    builder.Services.AddHangfire((services, settings) => settings.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
        .UseSqlServerStorage(services.GetRequiredService<IConfiguration>().GetConnectionString("Scheduling")
            ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required."), SchedulingRegistration.StorageOptions(prepareSchema: false)));
    builder.Services.AddScoped<AutomaticSpeedJob>();
    builder.Services.AddHostedService<SpeedSchedule>();
    builder.Services.AddHangfireServer(options => { options.Queues = ["network"]; options.IsLightweightServer = true; });
}
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok());
app.Run();

public partial class Program;
