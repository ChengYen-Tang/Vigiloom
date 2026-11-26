using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Vigiloom.Contracts.Agent;
using Vigiloom.HostMonitoring;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddModuleSqlServer<HostMonitoringDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("HostMonitoring")
        ?? throw new InvalidOperationException("ConnectionStrings:HostMonitoring is required."),
    "agent", pooled: true, migrationHistory: HostMonitoringDbContext.MigrationHistory);
builder.Services.AddServiceDiscovery();
builder.Services.AddHttpClient("resources", (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
var signalR = builder.Services.AddSignalR();
if (builder.Configuration.GetConnectionString("SignalR") is { Length: > 0 } backplane)
    signalR.AddStackExchangeRedis(backplane);
if (builder.Configuration.GetValue("Temporal:Enabled", true))
{
    builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
        builder.Configuration["Temporal:Namespace"] ?? "default");
    builder.Services.AddVigiloomTemporalWorker<AgentChangeHandler>(builder.Configuration["Work:AgentQueue"] ?? "vigiloom.agent.resources");
}
var app = builder.Build();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/v1/agent/channel"), channel =>
    channel.Use(async (context, next) =>
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("resources");
        using var response = await client.PostAsJsonAsync("internal/v1/agents/authenticate",
            new AgentCredentialCommand(header[7..]), context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var identity = await response.Content.ReadFromJsonAsync<RegisteredAgentIdentity>(cancellationToken: context.RequestAborted);
        if (identity is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, identity.AgentId.ToString("D"))], "Agent"));
        await next(context);
    }));
app.MapHub<AgentChannel>("/api/v1/agent/channel");
app.MapPost("/internal/v1/agents/{id:guid}/notifications/{kind}", async (Guid id, string kind, IHubContext<AgentChannel> hub, CancellationToken token) =>
{
    var method = kind switch { "update" => "UpdateAvailable", "scan" => "ScanAvailable", _ => null };
    if (method is null) return Results.BadRequest();
    await hub.Clients.Group(AgentChannel.Group(id)).SendAsync(method, token);
    return Results.Accepted();
});
app.Run();

public partial class Program;
