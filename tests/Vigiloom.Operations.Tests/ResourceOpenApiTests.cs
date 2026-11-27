extern alias ResourcesHost;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Vigiloom.Operations.Tests;

[TestClass]
public sealed class ResourceOpenApiTests(TestContext context)
{
    [TestMethod]
    public async Task DocumentsExternalPoliciesWithoutInternalEndpoints()
    {
        using var factory = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureTestServices(HostWorkers.RemoveBackgroundWork);
        });
        using var client = factory.CreateClient();
        using var document = await client.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", context.CancellationToken);
        var paths = document!.RootElement.GetProperty("paths");
        Assert.IsFalse(paths.EnumerateObject().Any(path => path.Name.StartsWith("/internal/", StringComparison.Ordinal)));
        var createOperation = paths.GetProperty("/api/v1/devices").GetProperty("post");
        Assert.Contains("monitoring.write", createOperation.GetProperty("description").GetString()!);
        Assert.IsTrue(createOperation.GetProperty("responses").GetProperty("201").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
        var create = createOperation.GetProperty("security");
        Assert.AreEqual(2, create.GetArrayLength());
        Assert.IsTrue(create[0].TryGetProperty("BrowserSession", out _));
        Assert.IsTrue(create[0].TryGetProperty("Antiforgery", out _));
        Assert.IsTrue(create[1].TryGetProperty("ApiBearer", out _));
        var recoverOperation = paths.GetProperty("/api/v1/agent/recover").GetProperty("post");
        Assert.Contains("enrollment secret", recoverOperation.GetProperty("description").GetString()!);
        var recover = recoverOperation.GetProperty("security");
        Assert.IsTrue(recover[0].TryGetProperty("AgentBearer", out _));
        var enroll = paths.GetProperty("/api/v1/agent/enroll").GetProperty("post");
        Assert.IsFalse(enroll.TryGetProperty("security", out var security) && security.GetArrayLength() > 0);
    }
}
