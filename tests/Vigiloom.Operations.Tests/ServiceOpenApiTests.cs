extern alias HostMonitoring;
extern alias NetworkHost;
extern alias ObservabilityHost;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Vigiloom.Operations.Tests;

[TestClass]
public sealed class ServiceOpenApiTests(TestContext context)
{
    [TestMethod]
    public async Task DocumentsOnlyPublicPathsAndTheirAuthorization()
    {
        await VerifyAsync<NetworkHost::Program>("/api/v1/devices/{id}/ping", "get", "ApiBearer");
        await VerifyAsync<HostMonitoring::Program>("/api/v1/agent/heartbeat", "post", "AgentBearer");
        await VerifyAsync<Program>("/api/v1/devices/{deviceId}/host-logs", "get", "ApiBearer");
        await VerifyAsync<ObservabilityHost::Program>("/api/v1/otlp/v1/logs", "post", "ServiceSourceBearer");
    }

    private async Task VerifyAsync<TProgram>(string path, string method, string scheme) where TProgram : class
    {
        using var factory = new WebApplicationFactory<TProgram>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.UseSetting("Hangfire:Enabled", "false");
            host.UseSetting("Temporal:Enabled", "false");
            host.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) &&
                    item.ImplementationType?.Namespace?.StartsWith("Vigiloom.", StringComparison.Ordinal) == true).ToArray())
                    services.Remove(descriptor);
                HostWorkers.RemoveBackgroundWork(services);
            });
        });
        using var client = factory.CreateClient();
        using var document = await client.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", context.CancellationToken);
        var paths = document!.RootElement.GetProperty("paths");
        Assert.IsFalse(paths.EnumerateObject().Any(item => item.Name.StartsWith("/internal/", StringComparison.Ordinal)));
        var security = paths.GetProperty(path).GetProperty(method).GetProperty("security");
        Assert.IsTrue(security.EnumerateArray().Any(item => item.TryGetProperty(scheme, out _)));
        if (path.EndsWith("/host-logs", StringComparison.Ordinal))
        {
            Assert.AreEqual(2, security.GetArrayLength());
            Assert.IsTrue(security.EnumerateArray().Any(item => item.TryGetProperty("BrowserSession", out _)));
        }
        if (scheme != "ServiceSourceBearer")
        {
            var schemaPath = scheme == "AgentBearer" ? "/api/v1/devices/{deviceId}/host-metrics" : path;
            var response = paths.GetProperty(schemaPath).GetProperty("get").GetProperty("responses").GetProperty("200");
            Assert.IsTrue(response.GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
        }
        if (scheme == "AgentBearer")
        {
            var thresholdWrite = paths.GetProperty("/api/v1/devices/{deviceId}/host-metric-threshold").GetProperty("put");
            Assert.AreEqual(1, thresholdWrite.GetProperty("security").GetArrayLength());
            Assert.IsTrue(thresholdWrite.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
            Assert.IsTrue(thresholdWrite.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
            Assert.IsFalse(thresholdWrite.GetProperty("security")[0].TryGetProperty("ApiBearer", out _));
            Assert.IsTrue(thresholdWrite.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
        }
    }
}
