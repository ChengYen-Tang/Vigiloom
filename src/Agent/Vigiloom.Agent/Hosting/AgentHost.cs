using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Hosting;

/// <summary>Composition root; no collection, update or scheduled work is enabled yet.</summary>
public static class AgentHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Agent");
        builder.Services.AddSystemd();
        builder.Services.AddHttpClient<IdentityClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 64 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });
        return builder;
    }
}
