extern alias ObservabilityHost;

using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class ObservabilityOtlpErrorsTests(TestContext context)
{
    [TestMethod]
    public async Task AuthenticationFailuresUseOtlpResponses()
    {
        using var host = new WebApplicationFactory<ObservabilityHost::Program>().WithWebHostBuilder(web =>
        {
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Observability"] = "Server=127.0.0.1,1;Database=Vigiloom_Unavailable;User Id=test;Password=test;Connect Timeout=1;TrustServerCertificate=true"
            }));
            web.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) &&
                    (item.ImplementationType?.Name is "HealthWorker" or "HealthPolicyWorker" or "HealthExpiryWorker" or "WorkDispatcher" ||
                     item.ImplementationFactory?.Method.Name.Contains("AddHostedTemporalWorker", StringComparison.Ordinal) == true)).ToArray())
                    services.Remove(descriptor);
            });
        });
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var missing = await client.PostAsync("/api/v1/otlp/v1/metrics", new ByteArrayContent([]), context.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.AreEqual("application/x-protobuf", missing.Content.Headers.ContentType?.MediaType);

        var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vls_{Guid.CreateVersion7():D}.{secret}");
        using var unavailable = await client.PostAsync("/api/v1/otlp/v1/metrics", new ByteArrayContent([]), context.CancellationToken);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.AreEqual("application/x-protobuf", unavailable.Content.Headers.ContentType?.MediaType);
    }
}
