using Vigiloom.Contracts.SpeedTests;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Web.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore(options => options.AddPolicy(WorkspaceAccess.MonitoringRead,
    policy => policy.RequireRole(WorkspaceAccess.Administrator, WorkspaceAccess.ReadOnly)));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();
builder.Services.AddMudServices();
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IWorkspaceIdentityClient, BrowserWorkspaceIdentityClient>();
builder.Services.AddScoped<ISpeedHistoryClient, BrowserSpeedHistoryClient>();

await builder.Build().RunAsync();
