extern alias HostMonitoring;
extern alias NetworkHost;

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
        var schemaPath = scheme == "AgentBearer" ? "/api/v1/devices/{deviceId}/host-metrics" : path;
        var response = paths.GetProperty(schemaPath).GetProperty("get").GetProperty("responses").GetProperty("200");
        Assert.IsTrue(response.GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
    }
}
