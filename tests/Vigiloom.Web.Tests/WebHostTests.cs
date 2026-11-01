using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class WebHostTests(TestContext testContext)
{
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
        var accessSecurity = paths.GetProperty("/api/v1/access").GetProperty("get").GetProperty("security")[0];
        Assert.IsTrue(accessSecurity.TryGetProperty("ApiBearer", out _));
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
}
