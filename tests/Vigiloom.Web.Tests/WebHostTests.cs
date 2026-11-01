using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class WebHostTests(TestContext testContext)
{
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
