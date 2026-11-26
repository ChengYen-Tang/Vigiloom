using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Security;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Security");
builder.Services.AddModuleSqlServer<SecurityAnalysisDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Security")
        ?? throw new InvalidOperationException("ConnectionStrings:Security is required."), "security", pooled: true);
builder.Services.AddServiceDiscovery();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<YaraScans>();
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
app.UseWhen(context => context.Request.Path.StartsWithSegments("/internal/v1/yara-scans"), branch =>
    branch.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (ArgumentException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
    }));
app.MapGet("/health/live", () => Results.Ok());
var yara = app.MapGroup("/internal/v1/yara-scans");
yara.MapPost("/create", async (YaraCreateCommand command, YaraScans scans, CancellationToken token) =>
    Results.Ok(await scans.CreateAsync(command.Identity, command.Request, command.ActorId, token)));
yara.MapPost("/next", async (RegisteredAgentIdentity identity, YaraScans scans, CancellationToken token) =>
    await scans.NextAsync(identity, token) is { } job ? Results.Ok(job) : Results.NoContent());
yara.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, YaraScans scans, CancellationToken token) =>
    Results.Ok(await scans.ListAsync(deviceId, token)));
yara.MapGet("/devices/{deviceId:guid}/{scanId:guid}", async (Guid deviceId, Guid scanId, YaraScans scans, CancellationToken token) =>
    await scans.GetAsync(deviceId, scanId, token) is { } job ? Results.Ok(job) : Results.NotFound());
yara.MapPost("/devices/{deviceId:guid}/{scanId:guid}/cancel", async (Guid deviceId, Guid scanId, YaraScans scans, CancellationToken token) =>
    await scans.CancelAsync(deviceId, scanId, token) ? Results.NoContent() : Results.NotFound());
app.Run();

public partial class Program;
