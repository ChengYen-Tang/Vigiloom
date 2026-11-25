using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Vigiloom.Contracts.Identity;
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
        Assert.AreEqual("/api/v1/public/devices?limit=5", await client.GetStringAsync("/api/v1/public/devices?limit=5", TestContext.CancellationToken));
        using var forbidden = await client.GetAsync("/internal/identity/sessions/validate", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [TestMethod]
    public async Task ResolvesConfiguredServiceToMultipleEndpoints()
    {
        await using var first = await StartUpstreamAsync("first", TestContext.CancellationToken);
        await using var second = await StartUpstreamAsync("second", TestContext.CancellationToken);
        static string HostPort(WebApplication app) => new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Authority;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = "http://test-service/",
                ["Services:test-service:http:0"] = HostPort(first),
                ["Services:test-service:http:1"] = HostPort(second)
            })));
        using var client = factory.CreateClient();
        var seen = new HashSet<string>();
        for (var attempt = 0; attempt < 40 && seen.Count < 2; attempt++)
            seen.Add(await client.GetStringAsync("/status", TestContext.CancellationToken));
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, seen.ToArray());
    }

    [TestMethod]
    public async Task RoutesAgentAndOtlpToTheirConfiguredServices()
    {
        await using var web = await StartUpstreamAsync("web", TestContext.CancellationToken);
        await using var agent = await StartUpstreamAsync("agent", TestContext.CancellationToken);
        await using var otlp = await StartUpstreamAsync("otlp", TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(web),
                ["ReverseProxy:Clusters:agent:Destinations:agent:Address"] = Address(agent),
                ["ReverseProxy:Clusters:observability:Destinations:observability:Address"] = Address(otlp)
            })));
        using var client = factory.CreateClient();
        Assert.AreEqual("web", await client.GetStringAsync("/status", TestContext.CancellationToken));
        Assert.AreEqual("agent", await client.GetStringAsync("/api/v1/agent/identity", TestContext.CancellationToken));
        Assert.AreEqual("otlp", await client.GetStringAsync("/api/v1/otlp/v1/metrics", TestContext.CancellationToken));
        using var denied = await client.GetAsync("/api/v1/agent-management/devices", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [TestMethod]
    [DataRow("/api/v1/devices")]
    [DataRow("/api/v1/groups")]
    public async Task ResourceRoutesEnforceApiCredentialPermissions(string path)
    {
        await using var web = await StartUpstreamAsync("web", TestContext.CancellationToken);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var identity = builder.Build();
        identity.MapPost("/internal/identity/api-credentials/validate", (VerifyApiCredentialRequest request) =>
            request.Value switch
            {
                "vlg_read" => Results.Json(new VerifiedApiCredentialResponse(Guid.CreateVersion7(), "read", false)),
                "vlg_write" => Results.Json(new VerifiedApiCredentialResponse(Guid.CreateVersion7(), "write", true)),
                _ => Results.Unauthorized()
            });
        await identity.StartAsync(TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(web),
                ["Services:Identity:Url"] = Address(identity)
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var anonymous = await client.GetAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_read");
        using var read = await client.GetAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        using var denied = await client.PostAsync(path, null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_write");
        using var write = await client.PostAsync(path, null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        foreach (var adminPath in new[] { "/api/v1/accounts", "/api/v1/agent-management/devices", "/api/v1/settings/ping" })
        {
            using var adminDenied = await client.GetAsync(adminPath, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Unauthorized, adminDenied.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_revoked");
        using var revoked = await client.GetAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [TestMethod]
    [DataRow("/api/v1/devices")]
    [DataRow("/api/v1/groups")]
    public async Task ResourceRoutesEnforceCookieRolesAndRevocation(string path)
    {
        await using var web = await StartUpstreamAsync("web", TestContext.CancellationToken);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var identity = builder.Build();
        var account = Guid.CreateVersion7();
        var role = "Read only";
        var revoked = false;
        identity.MapPost("/internal/identity/cookies/validate", () => revoked ? Results.Unauthorized() : Results.Json(new ValidatedCookieResponse([
            new(ClaimTypes.NameIdentifier, account.ToString("D"), ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY"),
            new(ClaimTypes.Role, role, ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY")
        ])));
        await identity.StartAsync(TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(web),
                ["Services:Identity:Url"] = Address(identity)
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var claims = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.ToString("D")), new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddHours(-7), ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
        var cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(claims), properties, IdentityConstants.ApplicationScheme));
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={cookie}");
        using var read = await client.GetAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.IsFalse(read.Headers.Contains("Set-Cookie"), "Gateway must not renew the Web session cookie.");
        foreach (var adminPath in new[] { "/api/v1/accounts", "/api/v1/agent-management/devices", "/api/v1/settings/ping" })
        {
            using var adminDenied = await client.GetAsync(adminPath, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Forbidden, adminDenied.StatusCode);
        }
        using var denied = await client.PostAsync(path, null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        role = "Administrator";
        foreach (var adminPath in new[] { "/api/v1/accounts", "/api/v1/agent-management/devices", "/api/v1/settings/ping" })
        {
            using var adminRead = await client.GetAsync(adminPath, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, adminRead.StatusCode);
        }
        using var write = await client.PostAsync(path, null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        revoked = true;
        using var rejected = await client.GetAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [TestMethod]
    public async Task PublicRoutesDoNotValidateSessionAtGateway()
    {
        await using var web = await StartUpstreamAsync("web", TestContext.CancellationToken);
        var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = address,
                ["Services:Identity:Url"] = "http://127.0.0.1:1"
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var claims = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString("D")), new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
        var cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(claims), IdentityConstants.ApplicationScheme));
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={cookie}");
        Assert.AreEqual("web", await client.GetStringAsync("/status", TestContext.CancellationToken));
    }

    private static async Task<WebApplication> StartUpstreamAsync(string name, CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], () => name);
        await app.StartAsync(token);
        return app;
    }

}
