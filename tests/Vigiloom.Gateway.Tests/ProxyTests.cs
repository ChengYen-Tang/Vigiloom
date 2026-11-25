using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Vigiloom.Gateway.Tests;

[TestClass]
public sealed class ProxyTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task ForwardsPublicPathsAndRejectsInternalPaths()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/{**path}", (HttpContext context) => Results.Text(context.Request.Path + context.Request.QueryString));
        await upstream.StartAsync(TestContext.CancellationToken);
        var address = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = address
            })));
        using var client = factory.CreateClient();
        Assert.AreEqual("/api/v1/devices?limit=5", await client.GetStringAsync("/api/v1/devices?limit=5", TestContext.CancellationToken));
        using var forbidden = await client.GetAsync("/internal/identity/sessions/validate", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, forbidden.StatusCode);
    }
}
