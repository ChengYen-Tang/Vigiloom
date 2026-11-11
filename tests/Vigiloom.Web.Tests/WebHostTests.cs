using System.Net;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class WebHostTests(TestContext testContext)
{
    [TestMethod]
    public async Task PublicStaticAssets_DoNotRevalidateAnAuthenticatedCookie()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<CountingValidator>();
                services.AddSingleton<ISecurityStampValidator>(provider => provider.GetRequiredService<CountingValidator>());
            });
        });
        using var client = CreateClient(factory);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim(ClaimTypes.Name, "test-user")], IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        }, IdentityConstants.ApplicationScheme);
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat!.Protect(ticket)}");
        var counter = factory.Services.GetRequiredService<CountingValidator>();
        using (var asset = await client.GetAsync("/_framework/blazor.web.js", testContext.CancellationToken)) Assert.AreEqual(HttpStatusCode.OK, asset.StatusCode);
        Assert.AreEqual(0, counter.Count);
        using (var page = await client.GetAsync("/", testContext.CancellationToken)) Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.IsGreaterThan(0, counter.Count);
    }

    [TestMethod]
    [DataRow("/_content/MudBlazor/MudBlazor.min.css")]
    [DataRow("/_content/MudBlazor/MudBlazor.min.js")]
    [DataRow("/_content/Vizor.ECharts/js/vizor-echarts-bundle-min.js")]
    public async Task SharedComponentAssets_AreAvailable(string path)
    {
        using var factory = CreateFactory("Development");
        using var client = CreateClient(factory);
        using var response = await client.GetAsync(path, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task ApiRateLimit_ReturnsProblemDetailsAndRetryAfter()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Authentication:ApiRequestsPerMinute"] = "2"
            }));
        });
        using var client = CreateClient(factory);
        for (var request = 0; request < 2; request++)
        {
            using var response = await client.GetAsync("/api/v1/access", testContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var rejected = await client.GetAsync("/api/v1/access", testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.AreEqual("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(TimeSpan.FromSeconds(60), rejected.Headers.RetryAfter?.Delta);
        using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(testContext.CancellationToken));
        Assert.AreEqual("too_many_requests", body.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task LoginRateLimit_ReturnsTooManyRequests()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateClient(factory);
        for (var request = 0; request < 30; request++)
        {
            using var allowed = await client.GetAsync("/account/login", testContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        }
        using var rejected = await client.GetAsync("/account/login", testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [TestMethod]
    public async Task Home_AutoComponent_IsPrerendered()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/", testContext.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Vigiloom</h1>", html);
        Assert.Contains("<!--Blazor:", html);
        Assert.Contains("\"type\":\"auto\"", html);
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    public async Task Liveness_WithoutConfiguredDatabase_ReturnsHealthy(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/health/live", testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync(testContext.CancellationToken));
    }

    [TestMethod]
    public async Task DatabaseHealth_WithoutConfiguredDatabase_IsUnhealthy()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateClient(factory);
        var checks = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await checks.CheckHealthAsync(check => check.Tags.Contains("platform"), testContext.CancellationToken);

        Assert.AreEqual(HealthStatus.Unhealthy, report.Status);
        Assert.AreEqual(HealthStatus.Unhealthy, report.Entries["database"].Status);
        using var live = await client.GetAsync("/health/live", testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
    }

    [TestMethod]
    public async Task OpenApi_InDevelopment_ReturnsDocument()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/openapi/v1.json", testContext.CancellationToken);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(testContext.CancellationToken));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(document.RootElement.GetProperty("openapi").GetString());
        Assert.AreEqual(JsonValueKind.Object, document.RootElement.GetProperty("paths").ValueKind);
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.AreEqual("bearer", schemes.GetProperty("ApiBearer").GetProperty("scheme").GetString());
        Assert.AreEqual("cookie", schemes.GetProperty("BrowserSession").GetProperty("in").GetString());
        var paths = document.RootElement.GetProperty("paths");
        var metricAgentSecurity = paths.GetProperty("/api/v1/agent/metric-settings").GetProperty("get").GetProperty("security");
        Assert.AreEqual(1, metricAgentSecurity.GetArrayLength());
        Assert.IsTrue(metricAgentSecurity[0].TryGetProperty("AgentBearer", out _));
        var inventorySecurity = paths.GetProperty("/api/v1/agent/inventory/status").GetProperty("get").GetProperty("security");
        Assert.IsTrue(inventorySecurity[0].TryGetProperty("AgentBearer", out _));
        var inventoryReadSecurity = paths.GetProperty("/api/v1/devices/{deviceId}/inventory").GetProperty("get").GetProperty("security");
        Assert.IsTrue(inventoryReadSecurity.EnumerateArray().Any(x => x.TryGetProperty("BrowserSession", out _)));
        Assert.IsTrue(inventoryReadSecurity.EnumerateArray().Any(x => x.TryGetProperty("ApiBearer", out _)));
        Assert.IsFalse(inventoryReadSecurity.EnumerateArray().Any(x => x.TryGetProperty("AgentBearer", out _)));
        var logAgentSecurity = paths.GetProperty("/api/v1/agent/log-settings").GetProperty("get").GetProperty("security");
        Assert.AreEqual(1, logAgentSecurity.GetArrayLength());
        Assert.IsTrue(logAgentSecurity[0].TryGetProperty("AgentBearer", out _));
        var hostLogSecurity = paths.GetProperty("/api/v1/devices/{deviceId}/host-logs").GetProperty("get").GetProperty("security");
        Assert.AreEqual(2, hostLogSecurity.GetArrayLength());
        Assert.IsTrue(hostLogSecurity.EnumerateArray().Any(x => x.TryGetProperty("BrowserSession", out _)));
        Assert.IsTrue(hostLogSecurity.EnumerateArray().Any(x => x.TryGetProperty("ApiBearer", out _)));
        var accessSecurity = paths.GetProperty("/api/v1/access").GetProperty("get").GetProperty("security")[0];
        Assert.IsTrue(accessSecurity.TryGetProperty("ApiBearer", out _));
        var audit = paths.GetProperty("/api/v1/operations/audit").GetProperty("get");
        var auditSecurity = audit.GetProperty("security");
        Assert.AreEqual(1, auditSecurity.GetArrayLength());
        Assert.IsTrue(auditSecurity[0].TryGetProperty("BrowserSession", out _));
        Assert.IsFalse(auditSecurity[0].TryGetProperty("ApiBearer", out _));
        Assert.IsFalse(auditSecurity[0].TryGetProperty("AgentBearer", out _));
        Assert.Contains("Administrator", audit.GetProperty("description").GetString()!);
        var replayAudit = paths.GetProperty("/api/v1/operations/audit/agent-replay").GetProperty("get");
        Assert.IsTrue(replayAudit.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", replayAudit.GetProperty("description").GetString()!);
        var inboxAudit = paths.GetProperty("/api/v1/operations/audit/agent-inbox").GetProperty("get");
        Assert.IsTrue(inboxAudit.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", inboxAudit.GetProperty("description").GetString()!);
        var inboxSettings = paths.GetProperty("/api/v1/settings/agent-inbox");
        Assert.IsTrue(inboxSettings.GetProperty("get").GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(inboxSettings.GetProperty("put").GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
        Assert.IsTrue(inboxSettings.GetProperty("put").GetProperty("responses").TryGetProperty("429", out _));
        var serviceAudit = paths.GetProperty("/api/v1/operations/audit/service-approvals").GetProperty("get");
        Assert.IsTrue(serviceAudit.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", serviceAudit.GetProperty("description").GetString()!);
        var candidateList = paths.GetProperty("/api/v1/service-discovery/candidates").GetProperty("get");
        var observation = paths.GetProperty("/api/v1/service-discovery/observations").GetProperty("post");
        Assert.IsTrue(observation.GetProperty("security")[0].TryGetProperty("ServiceSourceBearer", out _));
        Assert.IsTrue(observation.GetProperty("responses").TryGetProperty("413", out _));
        Assert.IsTrue(observation.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.IsTrue(candidateList.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", candidateList.GetProperty("description").GetString()!);
        Assert.IsTrue(candidateList.GetProperty("responses").TryGetProperty("429", out _));
        var template = paths.GetProperty("/api/v1/service-reception/collector-template").GetProperty("get");
        Assert.IsTrue(template.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", template.GetProperty("description").GetString()!);
        Assert.IsTrue(template.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("application/yaml", out _));
        Assert.IsTrue(template.GetProperty("responses").TryGetProperty("401", out _));
        Assert.IsTrue(template.GetProperty("responses").TryGetProperty("403", out _));
        Assert.IsTrue(template.GetProperty("responses").TryGetProperty("429", out _));
        var applicationTemplate = paths.GetProperty("/api/v1/service-reception/application-template").GetProperty("get");
        Assert.IsTrue(applicationTemplate.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", applicationTemplate.GetProperty("description").GetString()!);
        Assert.IsTrue(applicationTemplate.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("text/plain", out _));
        Assert.IsTrue(applicationTemplate.GetProperty("responses").TryGetProperty("401", out _));
        Assert.IsTrue(applicationTemplate.GetProperty("responses").TryGetProperty("403", out _));
        Assert.IsTrue(applicationTemplate.GetProperty("responses").TryGetProperty("429", out _));
        var collectorObservation = paths.GetProperty("/api/v1/service-reception/collector-observations/{serviceId}").GetProperty("get");
        Assert.IsTrue(collectorObservation.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", collectorObservation.GetProperty("description").GetString()!);
        var approveCandidate = paths.GetProperty("/api/v1/service-discovery/candidates/{candidateId}/approve").GetProperty("post");
        Assert.IsTrue(approveCandidate.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(approveCandidate.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
        Assert.IsTrue(approveCandidate.GetProperty("responses").TryGetProperty("429", out _));
        Assert.IsTrue(approveCandidate.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("application/json", out _));
        var instancePolicy = paths.GetProperty("/api/v1/service-instances/policy");
        var instancePolicyWrite = instancePolicy.GetProperty("put");
        Assert.IsTrue(instancePolicyWrite.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(instancePolicyWrite.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
        Assert.IsTrue(instancePolicyWrite.GetProperty("responses").TryGetProperty("429", out _));
        Assert.Contains("Administrator", instancePolicyWrite.GetProperty("description").GetString()!);
        var instanceAudit = paths.GetProperty("/api/v1/operations/audit/service-instances").GetProperty("get");
        Assert.IsTrue(instanceAudit.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", instanceAudit.GetProperty("description").GetString()!);
        var serviceAction = paths.GetProperty("/api/v1/service-discovery/services/{serviceId}/{action}").GetProperty("post");
        Assert.IsTrue(serviceAction.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(serviceAction.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
        Assert.IsTrue(serviceAction.GetProperty("responses").TryGetProperty("429", out _));
        var replayMutation = paths.GetProperty("/api/v1/agent-management/replay-policy").GetProperty("put").GetProperty("security")[0];
        Assert.IsTrue(replayMutation.TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(replayMutation.TryGetProperty("Antiforgery", out _));
        var feed = paths.GetProperty("/api/v1/operations/audit-feed/{source}").GetProperty("get");
        var feedSecurity = feed.GetProperty("security");
        Assert.AreEqual(1, feedSecurity.GetArrayLength());
        Assert.IsTrue(feedSecurity[0].TryGetProperty("BrowserSession", out _));
        Assert.Contains("Administrator", feed.GetProperty("description").GetString()!);
        var platform = paths.GetProperty("/api/v1/operations/platform-status").GetProperty("get");
        var platformSecurity = platform.GetProperty("security");
        Assert.AreEqual(1, platformSecurity.GetArrayLength());
        Assert.IsTrue(platformSecurity[0].TryGetProperty("BrowserSession", out _));
        Assert.IsFalse(platformSecurity[0].TryGetProperty("ApiBearer", out _));
        Assert.IsFalse(platformSecurity[0].TryGetProperty("AgentBearer", out _));
        Assert.Contains("Administrator", platform.GetProperty("description").GetString()!);
        var deviceRead = paths.GetProperty("/api/v1/devices").GetProperty("get");
        var deviceReadSecurity = deviceRead.GetProperty("security");
        Assert.IsTrue(deviceReadSecurity[0].TryGetProperty("BrowserSession", out _));
        Assert.IsFalse(deviceReadSecurity[0].TryGetProperty("Antiforgery", out _));
        Assert.IsTrue(deviceReadSecurity[1].TryGetProperty("ApiBearer", out _));
        Assert.Contains("monitoring.read", deviceRead.GetProperty("description").GetString()!);
        var packages = paths.GetProperty("/api/v1/agent-packages").GetProperty("get");
        Assert.IsTrue(packages.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(packages.GetProperty("security")[1].TryGetProperty("ApiBearer", out _));
        var packageCache = paths.GetProperty("/api/v1/agent-packages/{id}/cache").GetProperty("post");
        Assert.IsTrue(packageCache.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(packageCache.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
        Assert.AreEqual(1, packageCache.GetProperty("security").GetArrayLength());
        Assert.IsFalse(packages.GetProperty("security")[0].TryGetProperty("AgentBearer", out _));
        Assert.Contains("monitoring.read", packages.GetProperty("description").GetString()!);
        var agentMetadata = paths.GetProperty("/api/v1/agent/update/metadata/{name}").GetProperty("get");
        Assert.IsTrue(agentMetadata.GetProperty("security")[0].TryGetProperty("AgentBearer", out _));
        Assert.IsFalse(agentMetadata.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        var packageSourceUpdate = paths.GetProperty("/api/v1/agent-management/package-source").GetProperty("put");
        Assert.IsTrue(packageSourceUpdate.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(packageSourceUpdate.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
        var deviceWrite = paths.GetProperty("/api/v1/devices").GetProperty("post");
        var deviceWriteSecurity = deviceWrite.GetProperty("security");
        Assert.IsTrue(deviceWriteSecurity[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(deviceWriteSecurity[0].TryGetProperty("Antiforgery", out _));
        Assert.IsTrue(deviceWriteSecurity[1].TryGetProperty("ApiBearer", out _));
        Assert.Contains("monitoring.write", deviceWrite.GetProperty("description").GetString()!);
        Assert.IsTrue(deviceWrite.GetProperty("responses").TryGetProperty("429", out _));
        var addressMatches = paths.GetProperty("/api/v1/devices/address-matches").GetProperty("get");
        Assert.Contains("monitoring.write", addressMatches.GetProperty("description").GetString()!);
        Assert.IsFalse(addressMatches.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
        var createSecurity = paths.GetProperty("/api/v1/accounts").GetProperty("post").GetProperty("security")[0];
        Assert.IsTrue(createSecurity.TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(createSecurity.TryGetProperty("Antiforgery", out _));
        var createResponses = paths.GetProperty("/api/v1/accounts").GetProperty("post").GetProperty("responses");
        Assert.Contains("AccountCreatedResponse", createResponses.GetProperty("201").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString() ?? "");
        foreach (var code in new[] { "400", "401", "403", "409", "429" })
            Assert.IsTrue(createResponses.GetProperty(code).GetProperty("content").TryGetProperty("application/problem+json", out _));
        var renameResponses = paths.GetProperty("/api/v1/accounts/{id}/name").GetProperty("put").GetProperty("responses");
        Assert.IsTrue(renameResponses.TryGetProperty("204", out _));
        var accessResponses = paths.GetProperty("/api/v1/access").GetProperty("get").GetProperty("responses");
        Assert.IsTrue(accessResponses.TryGetProperty("401", out _));
        Assert.IsTrue(accessResponses.TryGetProperty("429", out _));
    }

    [TestMethod]
    public async Task OpenApi_InProduction_IsNotExposed()
    {
        using var factory = CreateFactory("Production");
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/openapi/v1.json", testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    public async Task MissingApi_ReturnsProblemDetails_InsteadOfHtml(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/v1/not-implemented", testContext.CancellationToken);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(testContext.CancellationToken));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(404, document.RootElement.GetProperty("status").GetInt32());
    }

    private static WebApplicationFactory<Program> CreateFactory(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

    private sealed class CountingValidator : ISecurityStampValidator
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public Task ValidateAsync(CookieValidatePrincipalContext context)
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        }
    }
}
