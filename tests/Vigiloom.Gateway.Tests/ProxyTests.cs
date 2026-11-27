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
    public async Task RoutesAgentSecurityEndpointsToHostMonitoring()
    {
        await using var host = await StartUpstreamAsync("host-monitoring", TestContext.CancellationToken);
        await using var legacy = await StartUpstreamAsync("existing-agent", TestContext.CancellationToken);
        var identityBuilder = WebApplication.CreateBuilder();
        identityBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var identity = identityBuilder.Build();
        identity.MapGet("/api/v1/agent/identity", () => Results.Json(new { AgentId = Guid.CreateVersion7(), DeviceId = Guid.CreateVersion7() }));
        await identity.StartAsync(TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = Address(host),
            ["ReverseProxy:Clusters:agent:Destinations:agent:Address"] = Address(legacy),
            ["Vigiloom:Gateway:AgentIdentityAddress"] = Address(identity)
        })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using (var denied = await client.GetAsync("/api/v1/agent/inventory/status", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-agent");
        foreach (var path in new[] { "/api/v1/agent/inventory-settings", "/api/v1/agent/inventory/status?contract=5", "/api/v1/agent/integrity-settings",
                     $"/api/v1/agent/integrity-status/{Guid.CreateVersion7():D}", "/api/v1/agent/baseline-settings", "/api/v1/agent/yara-scans/next" })
            Assert.AreEqual("host-monitoring", await client.GetStringAsync(path, TestContext.CancellationToken));
        using (var batches = await client.PostAsync("/api/v1/agent/batches", null, TestContext.CancellationToken))
            Assert.AreEqual("host-monitoring", await batches.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/version-assignment", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/update/targets/manifest.json", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/update/packages/agent.zip", TestContext.CancellationToken));
    }
    [TestMethod]
    public async Task RoutesAgentMetricSettingsToHostAfterAuthentication()
    {
        await using var host = await StartUpstreamAsync("host-monitoring", TestContext.CancellationToken);
        var identityBuilder = WebApplication.CreateBuilder();
        identityBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var identity = identityBuilder.Build();
        identity.MapGet("/api/v1/agent/identity", () => Results.Json(new { AgentId = Guid.CreateVersion7(), DeviceId = Guid.CreateVersion7() }));
        await identity.StartAsync(TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = Address(host),
            ["ReverseProxy:Routes:agent-batches:ClusterId"] = "host-monitoring",
            ["Vigiloom:Gateway:AgentIdentityAddress"] = Address(identity)
        })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using (var denied = await client.GetAsync("/api/v1/agent/metric-settings", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-agent");
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/metric-settings", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/log-settings", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/settings", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/queue-capacity", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync("/api/v1/agent/queue-session", TestContext.CancellationToken));
        foreach (var path in new[] { "/api/v1/agent/queue-session", "/api/v1/agent/heartbeat", "/api/v1/agent/update-results", "/api/v1/agent/batches" })
        {
            using var response = await client.PostAsync(path, null, TestContext.CancellationToken);
            Assert.AreEqual("host-monitoring", await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        }
    }

    [TestMethod]
    public async Task HostAgentTransportKeepsPerAgentRateLimit()
    {
        await using var host = await StartUpstreamAsync("host-monitoring", TestContext.CancellationToken);
        var identityBuilder = WebApplication.CreateBuilder();
        identityBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var identity = identityBuilder.Build();
        var agentId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();
        identity.MapGet("/api/v1/agent/identity", () => Results.Json(new { AgentId = agentId, DeviceId = deviceId }));
        await identity.StartAsync(TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = Address(host),
            ["Vigiloom:Gateway:AgentIdentityAddress"] = Address(identity),
            ["Vigiloom:Agent:Transport:RequestsPerMinute"] = "1"
        })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-agent");
        using (var first = await client.PostAsync("/api/v1/agent/heartbeat", null, TestContext.CancellationToken)) Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        using var limited = await client.GetAsync("/api/v1/agent/inventory/status", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("60", limited.Headers.RetryAfter!.ToString());
    }
    [TestMethod]
    public async Task RoutesAgentChannelOnlyOverHttps()
    {
        await using var host = await StartUpstreamAsync("host-monitoring", TestContext.CancellationToken);
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = address
            })));
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.AreEqual("host-monitoring", await https.GetStringAsync("/api/v1/agent/channel", TestContext.CancellationToken));
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        using var denied = await http.GetAsync("/api/v1/agent/channel", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
    [TestMethod]
    public async Task RoutesNetworkReadsWithoutExposingInternalOrAdministrativeEndpoints()
    {
        await using var web = await StartUpstreamAsync("web", TestContext.CancellationToken);
        await using var network = await StartUpstreamAsync("network", TestContext.CancellationToken);
        await using var resources = await StartUpstreamAsync("resources", TestContext.CancellationToken);
        await using var hostMonitoring = await StartUpstreamAsync("host-monitoring", TestContext.CancellationToken);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var identity = builder.Build();
        identity.MapPost("/internal/identity/api-credentials/validate", () => Results.Json(new VerifiedApiCredentialResponse(Guid.CreateVersion7(), "read", false)));
        await identity.StartAsync(TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(web),
                ["ReverseProxy:Clusters:network:Destinations:network:Address"] = Address(network),
                ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = Address(resources),
                ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = Address(hostMonitoring),
                ["Services:Identity:Url"] = Address(identity)
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var id = Guid.CreateVersion7();
        Assert.AreEqual("network", await client.GetStringAsync($"/api/v1/public/devices/{id:D}/ping", TestContext.CancellationToken));
        using (var response = await client.GetAsync($"/api/v1/devices/{id:D}/ping", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_read");
        Assert.AreEqual("network", await client.GetStringAsync($"/api/v1/devices/{id:D}/ping", TestContext.CancellationToken));
        Assert.AreEqual("network", await client.GetStringAsync($"/api/v1/devices/{id:D}/ping/history", TestContext.CancellationToken));
        Assert.AreEqual("network", await client.GetStringAsync("/api/v1/speed-tests", TestContext.CancellationToken));
        Assert.AreEqual("resources", await client.GetStringAsync($"/api/v1/devices/{id:D}", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync($"/api/v1/devices/{id:D}/agent-status", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync($"/api/v1/devices/{id:D}/agent-settings-status", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync($"/api/v1/devices/{id:D}/agent-queue-capacity-status", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync($"/api/v1/devices/{id:D}/host-metrics", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync($"/api/v1/devices/{id:D}/host-metrics/series", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync($"/api/v1/devices/{id:D}/agent-settings", TestContext.CancellationToken));
        Assert.AreEqual("host-monitoring", await client.GetStringAsync($"/api/v1/devices/{id:D}/agent-queue-capacity", TestContext.CancellationToken));
        using (var response = await client.GetAsync("/api/v1/agent-management/settings/platform", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync("/api/v1/agent-management/queue-capacity/platform", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync("/api/v1/agent-management/metric-settings/platform", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync("/api/v1/settings/ping", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.PostAsync($"/api/v1/speed-tests/{id:D}/cancel", null, TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync("/internal/v1/ping-targets", TestContext.CancellationToken))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
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
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var seen = new HashSet<string>();
        for (var attempt = 0; attempt < 40 && seen.Count < 2; attempt++)
            seen.Add(await client.GetStringAsync("/status", TestContext.CancellationToken));
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, seen.ToArray());
    }

    [TestMethod]
    public async Task KeepsWebSessionOnTheSameDiscoveredEndpoint()
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
        var selected = await client.GetStringAsync("/status", TestContext.CancellationToken);
        for (var attempt = 0; attempt < 20; attempt++)
            Assert.AreEqual(selected, await client.GetStringAsync("/_blazor/negotiate", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task RoutesAgentAndOtlpToTheirConfiguredServices()
    {
        await using var web = await StartUpstreamAsync("web", TestContext.CancellationToken);
        await using var agent = await StartUpstreamAsync("agent", TestContext.CancellationToken);
        await using var resources = await StartUpstreamAsync("resources", TestContext.CancellationToken);
        await using var otlp = await StartUpstreamAsync("otlp", TestContext.CancellationToken);
        static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(web),
                ["ReverseProxy:Clusters:agent:Destinations:agent:Address"] = Address(agent),
                ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = Address(resources),
                ["ReverseProxy:Clusters:observability:Destinations:observability:Address"] = Address(otlp),
                ["Vigiloom:Gateway:SourceIdentityAddress"] = Address(otlp)
            })));
        using var client = factory.CreateClient();
        Assert.AreEqual("web", await client.GetStringAsync("/status", TestContext.CancellationToken));
        using var agentDenied = await client.GetAsync("/api/v1/agent/identity", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, agentDenied.StatusCode);
        using var enrollment = await client.PostAsync("/api/v1/agent/enroll", null, TestContext.CancellationToken);
        Assert.AreEqual("resources", await enrollment.Content.ReadAsStringAsync(TestContext.CancellationToken));
        using var otlpDenied = await client.GetAsync("/api/v1/otlp/v1/metrics", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, otlpDenied.StatusCode);
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
                ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = Address(web),
                ["Services:Identity:Url"] = Address(identity)
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var anonymous = await client.GetAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousApi = await client.GetAsync("/api/v1/events", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousApi.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_read");
        using var read = await client.GetAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        using var generalRead = await client.GetAsync("/api/v1/events", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, generalRead.StatusCode);
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
                ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = Address(web),
                ["ReverseProxy:Clusters:network:Destinations:network:Address"] = Address(web),
                ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = Address(web),
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
        using var ownPassword = await client.PutAsync("/api/v1/session/password", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, ownPassword.StatusCode);
        Assert.IsFalse(read.Headers.TryGetValues("Set-Cookie", out var setCookies) &&
            setCookies.Any(value => value.StartsWith(options.Cookie.Name + "=", StringComparison.Ordinal)),
            "Gateway must not renew the Web session cookie.");
        foreach (var adminPath in new[] { "/api/v1/accounts", "/api/v1/agent-management/devices", "/api/v1/settings/ping" })
        {
            using var adminDenied = await client.GetAsync(adminPath, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Forbidden, adminDenied.StatusCode);
        }
        using var denied = await client.PostAsync(path, null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        role = "Administrator";
        Assert.AreEqual("web", await client.GetStringAsync("/api/v1/agent-management/metric-settings/platform", TestContext.CancellationToken));
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
        Assert.AreEqual("web", await client.GetStringAsync("/api/v1/public/status", TestContext.CancellationToken));
        using var webhook = await client.PostAsync("/api/v1/line/webhook", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, webhook.StatusCode);
    }

    [TestMethod]
    public async Task AgentRequestsRequireValidatedCredentialExceptEnrollmentAndRecovery()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var agent = builder.Build();
        var sawSecureForward = false;
        agent.MapGet("/api/v1/agent/identity", (HttpContext context) =>
        {
            sawSecureForward = context.Request.Headers["X-Forwarded-Proto"] == "https";
            if (context.Request.Headers.Authorization == "Bearer limited-agent")
            {
                context.Response.Headers.RetryAfter = "25";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            return context.Request.Headers.Authorization == "Bearer valid-agent"
                ? Results.Json(new { AgentId = Guid.CreateVersion7(), DeviceId = Guid.CreateVersion7() })
                : Results.Unauthorized();
        });
        agent.MapPost("/api/v1/agent/heartbeat", () => Results.NoContent());
        agent.MapPost("/api/v1/agent/enroll", () => Results.Ok());
        agent.MapPost("/api/v1/agent/recover", () => Results.Ok());
        agent.MapPost("/api/v1/agent/rotation/start", () => Results.NoContent());
        agent.MapPost("/api/v1/agent/rotation/finish", (HttpContext context) =>
            context.Request.Headers.Authorization == "Bearer pending-agent" ? Results.NoContent() : Results.Unauthorized());
        await agent.StartAsync(TestContext.CancellationToken);
        var address = agent.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:agent:Destinations:agent:Address"] = address,
                ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = address,
                ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = address,
                ["Vigiloom:Gateway:AgentIdentityAddress"] = address
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var missing = await client.PostAsync("/api/v1/agent/heartbeat", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-agent");
        using var invalid = await client.PostAsync("/api/v1/agent/heartbeat", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalid.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "limited-agent");
        using var limited = await client.PostAsync("/api/v1/agent/heartbeat", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("25", limited.Headers.RetryAfter?.ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-agent");
        using var valid = await client.PostAsync("/api/v1/agent/heartbeat", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, valid.StatusCode);
        Assert.IsTrue(sawSecureForward);
        client.DefaultRequestHeaders.Authorization = null;
        using var enrollment = await client.PostAsync("/api/v1/agent/enroll", null, TestContext.CancellationToken);
        using var recovery = await client.PostAsync("/api/v1/agent/recover", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, enrollment.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, recovery.StatusCode);
        using var rotationStart = await client.PostAsync("/api/v1/agent/rotation/start", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, rotationStart.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pending-agent");
        using var rotationFinish = await client.PostAsync("/api/v1/agent/rotation/finish", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, rotationFinish.StatusCode);
    }

    [TestMethod]
    public async Task OtlpAndDiscoveryRequireSourceCredentialAndForwardPayload()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var observability = builder.Build();
        var sourceId = Guid.CreateVersion7();
        var received = Array.Empty<byte>();
        observability.MapGet("/internal/telemetry/source/identity", (HttpContext context) =>
            context.Request.Headers.Authorization == "Bearer valid-source"
                ? Results.Json(new { SourceId = sourceId }) : Results.Unauthorized());
        observability.MapPost("/api/v1/otlp/v1/metrics", async (HttpContext context) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            received = buffer.ToArray();
            return Results.Bytes([9, 8], "application/x-protobuf");
        });
        observability.MapPost("/api/v1/service-discovery/observations", () => Results.NoContent());
        await observability.StartAsync(TestContext.CancellationToken);
        var address = observability.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:observability:Destinations:observability:Address"] = address,
                ["Vigiloom:Gateway:SourceIdentityAddress"] = address
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var missing = await client.PostAsync("/api/v1/otlp/v1/metrics", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.AreEqual("application/x-protobuf", missing.Content.Headers.ContentType?.MediaType);
        var failure = Google.Rpc.Status.Parser.ParseFrom(await missing.Content.ReadAsByteArrayAsync(TestContext.CancellationToken));
        Assert.IsFalse(string.IsNullOrEmpty(failure.Message));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-source");
        using var invalid = await client.PostAsync("/api/v1/service-discovery/observations", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalid.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-source");
        using var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var result = await client.PostAsync("/api/v1/otlp/v1/metrics", content, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, received);
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, await result.Content.ReadAsByteArrayAsync(TestContext.CancellationToken));
        using var discovery = await client.PostAsync("/api/v1/service-discovery/observations", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, discovery.StatusCode);
        using var internalEndpoint = await client.GetAsync("/internal/telemetry/source/identity", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, internalEndpoint.StatusCode);
        using var insecure = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        insecure.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-source");
        using var insecureResult = await insecure.PostAsync("/api/v1/otlp/v1/metrics", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, insecureResult.StatusCode);
    }

    [TestMethod]
    public async Task OtlpIdentityOutageReturnsProtobufUnavailable()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Gateway:SourceIdentityAddress"] = "http://127.0.0.1:1"
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-source");
        using var response = await client.PostAsync("/api/v1/otlp/v1/metrics", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.IsFalse(string.IsNullOrEmpty(Google.Rpc.Status.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(TestContext.CancellationToken)).Message));
    }

    [TestMethod]
    public async Task PublicStatusStreamForwardsEventsWithoutBuffering()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var web = builder.Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        web.MapGet("/api/v1/public/devices/{id}/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: second\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        });
        await web.StartAsync(TestContext.CancellationToken);
        var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:web:Destinations:web:Address"] = address
            })));
        using var client = factory.CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await client.GetAsync($"/api/v1/public/devices/{Guid.CreateVersion7():D}/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream);
            Assert.AreEqual("data: first", await reader.ReadLineAsync(timeout.Token));
            Assert.AreEqual("", await reader.ReadLineAsync(timeout.Token));
            release.TrySetResult();
            Assert.AreEqual("data: second", await reader.ReadLineAsync(timeout.Token));
        }
        finally { release.TrySetResult(); }
    }

    private static async Task<WebApplication> StartUpstreamAsync(string name, CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/internal/telemetry/source/identity", () => Results.Unauthorized());
        app.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], () => name);
        await app.StartAsync(token);
        return app;
    }

}
