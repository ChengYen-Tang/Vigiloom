extern alias HostMonitoring;
extern alias SecurityHost;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.Tests.Fixtures;

// The legacy batch fixture and Security share the test database during this transition.
internal sealed class InventoryStatusBridge : IDisposable
{
    private readonly WebApplicationFactory<SecurityHost::Program> security;
    private readonly WebApplicationFactory<HostMonitoring::Program> host = null!;
    public HttpClient Client { get; }

    public InventoryStatusBridge(string connectionString, IServiceProvider legacyServices)
    {
        security = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:Security", connectionString);
            web.UseSetting("Temporal:Enabled", "false");
        });
        try
        {
            using var securityClient = security.CreateClient();
            host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.UseSetting("Hangfire:Enabled", "false");
                web.ConfigureTestServices(services =>
                {
                    services.AddHttpClient("security").ConfigurePrimaryHttpMessageHandler(() => security.Server.CreateHandler());
                    services.AddHttpClient("resources").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(legacyServices));
                });
            });
            Client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });
        }
        catch
        {
            host?.Dispose();
            security.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        host.Dispose();
        security.Dispose();
    }

    private sealed class IdentityHandler(IServiceProvider services) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var command = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
            await using var scope = services.CreateAsyncScope();
            var identity = await scope.ServiceProvider.GetRequiredService<AgentIdentity>().AuthenticateAsync(command?.Credential, token);
            return identity is null ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RegisteredAgentIdentity(identity.AgentId, identity.DeviceId, identity.DeviceGeneration))
            };
        }
    }
}
