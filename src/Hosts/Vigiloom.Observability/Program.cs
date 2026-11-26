using Vigiloom.Contracts.Services;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Observability;

var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration.GetConnectionString("Observability")
    ?? throw new InvalidOperationException("ConnectionStrings:Observability is required.");
builder.Services.AddModuleSqlServer<ServiceObservabilityDbContext>(_ => database, "service", pooled: true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddServiceDiscovery();
builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
    builder.Configuration["Temporal:Namespace"] ?? "default");
builder.Services.AddScoped(provider => new OutboxWriter(provider.GetServices<IOutboxHandler>(), maxPayloadLength: null));
builder.Services.AddHttpClient<TemporalHealthTransitionSink>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddSingleton<IHealthTransitionSink, HealthTransitionDispatcher>();
builder.Services.AddSingleton<HealthRunner>();
builder.Services.AddScoped(provider => new OutboxDispatcher(database, provider.GetServices<IOutboxHandler>(),
    provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
builder.Services.AddHostedService<HealthWorker>();
builder.Services.AddHostedService<HealthPolicyWorker>();
builder.Services.AddHostedService<HealthExpiryWorker>();
builder.Services.AddHostedService<WorkDispatcher>();
var app = builder.Build();
app.Run();

public partial class Program;
