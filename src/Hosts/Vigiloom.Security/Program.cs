using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Security;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Security");
builder.Services.AddModuleSqlServer<SecurityAnalysisDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Security")
        ?? throw new InvalidOperationException("ConnectionStrings:Security is required."), "security", pooled: true);
builder.Services.AddServiceDiscovery();
builder.Services.AddHttpClient("host", (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Host:Url"] ?? "http://vigiloom-host-monitoring";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
if (builder.Configuration.GetValue("Temporal:Enabled", true))
{
    builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
        builder.Configuration["Temporal:Namespace"] ?? "default");
    builder.Services.AddVigiloomTemporalWorker<SecurityWorkHandler>(builder.Configuration["Work:SecurityQueue"] ?? "vigiloom.security.agent");
}

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok());
app.Run();

public partial class Program;
