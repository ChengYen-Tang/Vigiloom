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
            var webhook = paths.GetProperty("/api/v1/line/webhook").GetProperty("post");
            Assert.IsTrue(webhook.GetProperty("security")[0].TryGetProperty("LineSignature", out _));
            var linePath = paths.TryGetProperty("/api/v1/admin/line/", out var line) ? line : paths.GetProperty("/api/v1/admin/line");
            var lineWrite = linePath.GetProperty("put").GetProperty("security")[0];
            Assert.IsTrue(lineWrite.TryGetProperty("BrowserSession", out _));
            Assert.IsTrue(lineWrite.TryGetProperty("Antiforgery", out _));
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
            var yaraAgent = paths.GetProperty("/api/v1/agent/yara-scans/next").GetProperty("get");
            Assert.IsTrue(yaraAgent.GetProperty("security")[0].TryGetProperty("AgentBearer", out _));
            Assert.IsTrue(yaraAgent.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
            var capacityStatus = paths.GetProperty("/api/v1/devices/{id}/agent-queue-capacity-status").GetProperty("get").GetProperty("security");
            Assert.IsTrue(capacityStatus.EnumerateArray().Any(item => item.TryGetProperty("ApiBearer", out _)));
            var capacityWrite = paths.GetProperty("/api/v1/agent-management/queue-capacity/platform").GetProperty("put");
            Assert.IsTrue(capacityWrite.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
            Assert.IsTrue(capacityWrite.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
            var metricSeriesSecurity = paths.GetProperty("/api/v1/devices/{deviceId}/host-metrics/series").GetProperty("get").GetProperty("security");
            Assert.IsTrue(metricSeriesSecurity.EnumerateArray().Any(x => x.TryGetProperty("BrowserSession", out _)));
            Assert.IsTrue(metricSeriesSecurity.EnumerateArray().Any(x => x.TryGetProperty("ApiBearer", out _)));
            var metricAdmin = paths.GetProperty("/api/v1/agent-management/metric-settings/platform").GetProperty("put");
            Assert.IsTrue(metricAdmin.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
            Assert.IsTrue(metricAdmin.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
            Assert.IsTrue(paths.GetProperty("/api/v1/agent/metric-settings").GetProperty("get").GetProperty("security")[0].TryGetProperty("AgentBearer", out _));
            var retentionWrite = paths.GetProperty("/api/v1/settings/host-metrics/retention").GetProperty("put").GetProperty("security")[0];
            Assert.IsTrue(retentionWrite.TryGetProperty("BrowserSession", out _));
            Assert.IsTrue(retentionWrite.TryGetProperty("Antiforgery", out _));
            var integrityStatus = paths.GetProperty("/api/v1/devices/{id}/integrity/settings-status").GetProperty("get");
            Assert.IsTrue(integrityStatus.GetProperty("security").EnumerateArray().Any(item => item.TryGetProperty("ApiBearer", out _)));
            Assert.IsTrue(integrityStatus.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
            var inventoryStatus = paths.GetProperty("/api/v1/devices/{id}/inventory-settings-status").GetProperty("get");
            Assert.IsTrue(inventoryStatus.GetProperty("security").EnumerateArray().Any(item => item.TryGetProperty("ApiBearer", out _)));
            var replayPath = paths.TryGetProperty("/api/v1/agent-management/replay-policy/", out var replay) ? replay : paths.GetProperty("/api/v1/agent-management/replay-policy");
            var replayWrite = replayPath.GetProperty("put").GetProperty("security")[0];
            Assert.IsTrue(replayWrite.TryGetProperty("BrowserSession", out _));
            Assert.IsTrue(replayWrite.TryGetProperty("Antiforgery", out _));
            var inboxPath = paths.TryGetProperty("/api/v1/settings/agent-inbox/", out var inbox) ? inbox : paths.GetProperty("/api/v1/settings/agent-inbox");
            var inboxWrite = inboxPath.GetProperty("put").GetProperty("security")[0];
            Assert.IsTrue(inboxWrite.TryGetProperty("BrowserSession", out _));
            Assert.IsTrue(inboxWrite.TryGetProperty("Antiforgery", out _));
            var thresholdWrite = paths.GetProperty("/api/v1/devices/{deviceId}/host-metric-threshold").GetProperty("put");
            Assert.AreEqual(1, thresholdWrite.GetProperty("security").GetArrayLength());
            Assert.IsTrue(thresholdWrite.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
            Assert.IsTrue(thresholdWrite.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
            Assert.IsFalse(thresholdWrite.GetProperty("security")[0].TryGetProperty("ApiBearer", out _));
            Assert.IsTrue(thresholdWrite.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
        }
    }
}
