using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddModuleSqlServer<ResourcesDbContext>(
    _ => builder.Configuration.GetConnectionString("Resources")
        ?? throw new InvalidOperationException("ConnectionStrings:Resources is required."), "resources", pooled: true);
builder.Services.AddScoped<DevicePingSource>();
builder.Services.AddScoped<DeviceAgentSource>();
builder.Services.AddScoped<GroupMembersReader>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeviceManagement>();

var app = builder.Build();
var internalApi = app.MapGroup("/internal/v1");
internalApi.MapGet("/ping-targets", (DevicePingSource source, CancellationToken token) => source.ReadEnabledAsync(token));
internalApi.MapGet("/devices/{id:guid}/agent", async (Guid id, DeviceAgentSource source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound());
internalApi.MapGet("/groups/{id:guid}/members", async (Guid id, GroupMembersReader source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } members ? Results.Ok(members) : Results.NotFound());
internalApi.MapDeviceManagement();
app.Run();

public partial class Program;
