using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Web.Gateway;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class TrustedProxyForwardingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("127.0.0.1", "https|198.51.100.23|vigiloom.example")]
    [DataRow("192.0.2.1", "http|127.0.0.1|internal.example")]
    public async Task OnlyConfiguredProxyCanForwardSchemeAndClientIp(string trustedProxy, string expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test");
        builder.Services.AddAuthorization();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vigiloom:Gateway:TrustedProxies:0"] = trustedProxy
        }).Build();
        await using var app = builder.Build();
        app.UseForwardedHeaders(TrustedProxyForwarding.Create(configuration)!);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/", (HttpContext context) => $"{context.Request.Scheme}|{context.Connection.RemoteIpAddress}|{context.Request.Host}");
        app.MapGet("/private", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync(TestContext.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Host = "internal.example";
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "vigiloom.example");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.23");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.AreEqual(expected, await client.GetStringAsync("/", TestContext.CancellationToken));
        using var redirect = await client.GetAsync("/private", TestContext.CancellationToken);
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.AreEqual(expected.Split('|')[2], redirect.Headers.Location!.Host);
    }
}
