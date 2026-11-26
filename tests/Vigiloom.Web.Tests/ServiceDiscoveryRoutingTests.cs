extern alias ResourcesHost;
extern alias WorkerHostAssembly;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Web.Devices;
using Vigiloom.Web.Groups;
using Vigiloom.Web.Publishing;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class ServiceDiscoveryRoutingTests(TestContext context)
{
    [TestMethod]
    public async Task WebResourceClientsResolveLogicalServiceName()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/probe", () => "resources");
        await upstream.StartAsync(context.CancellationToken);
        var address = new Uri(upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:Resources:Url"] = "http://test-resources",
                ["Services:test-resources:http:0"] = address.Authority
            })));
        using var startup = web.CreateClient();
        var clients = web.Services.GetRequiredService<IHttpClientFactory>();
        foreach (var name in new[] { nameof(RemoteDeviceManagement), nameof(RemoteGroupManagement), nameof(RemotePublicReader) })
        {
            using var client = clients.CreateClient(name);
            Assert.AreEqual("resources", await client.GetStringAsync("/probe", context.CancellationToken));
        }
    }

    [TestMethod]
    public async Task ResourceClientsResolveLogicalServiceName()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/probe", () => "host");
        await upstream.StartAsync(context.CancellationToken);
        var address = new Uri(upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:Observability:Url"] = "http://test-host",
                ["Services:test-host:http:0"] = address.Authority
            })));
        using var startup = resources.CreateClient();
        var clients = resources.Services.GetRequiredService<IHttpClientFactory>();
        foreach (var name in new[] { "IPackageRuntimeUsage", "IHostAgentVersion", "ObservationSourceVerifier" })
        {
            using var client = clients.CreateClient(name);
            Assert.AreEqual("host", await client.GetStringAsync("http://test-host/probe", context.CancellationToken));
        }
    }

    [TestMethod]
    public async Task WorkerIdentityClientResolvesLogicalServiceName()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/probe", () => "identity");
        await upstream.StartAsync(context.CancellationToken);
        var address = new Uri(upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var worker = WorkerHostAssembly::Vigiloom.Worker.Hosting.WorkerHost.CreateBuilder([]);
        worker.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:Identity:Url"] = "http://test-identity",
            ["Services:test-identity:http:0"] = address.Authority
        });
        using var host = worker.Build();
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("RemoteIdentityAuditRetention");
        Assert.AreEqual("identity", await client.GetStringAsync("/probe", context.CancellationToken));
    }
}

