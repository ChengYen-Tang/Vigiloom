using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Web.Publishing;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteObservationPublicTests(TestContext context)
{
    [TestMethod]
    public async Task ReadsPublicStatusAndHistoryAndHidesWithdrawnPublication()
    {
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var visible = true;
        var revokeOnStatus = false;
        var revokeOnHistory = false;
        var policy = new PublishedServicePolicy(id, "Public app", PublicationPolicy.ServiceHistoryDetailsVersion, now.AddDays(-1), "version");
        using var resources = new HttpClient(new Handler(request => visible
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(policy) }
            : new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new Uri("http://resources/") };
        using var observations = new HttpClient(new Handler(request =>
        {
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/internal/v1/public-observation/versions" => new[] { new PublicObservationVersions(id, [1], [2]) },
                "/internal/v1/public-observation/statuses" => Status(),
                "/internal/v1/public-observation/time" => now,
                _ when request.RequestUri.AbsolutePath.EndsWith("/history", StringComparison.Ordinal) => History(),
                _ => throw new InvalidOperationException("Unexpected observation path.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        })) { BaseAddress = new Uri("http://observability/") };
        object Status()
        {
            if (revokeOnStatus) visible = false;
            return new Dictionary<Guid, HealthStatusReader.OverviewStatus>
            { [id] = new(new ServiceHealthStatus(ServiceHealthState.Healthy, 1, 0), now, false, false) };
        }
        object History()
        {
            if (revokeOnHistory) visible = false;
            return new ServiceHealthHistory([new(now.AddHours(-1), now, ServiceHealthState.Healthy, 0)], 1, 1);
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Resources:Url"] = "http://resources", ["Services:Observability:Url"] = "http://observability" }).Build();
        using var provider = new ServiceCollection().AddSingleton(new PublicObservationClient(observations))
            .AddSingleton<RemotePublicReader>(services => new RemotePublicReader(resources, config, services))
            .BuildServiceProvider();
        var reader = provider.GetRequiredService<RemotePublicReader>();
        Assert.AreEqual("Healthy", (await reader.ServiceAsync(id, context.CancellationToken))!.State);
        var history = new PublicServiceHistoryReader(null!, null!, config, provider);
        Assert.IsNotNull(await history.ReadAsync(id, now.AddHours(-1), now, context.CancellationToken));
        revokeOnStatus = true;
        Assert.IsNull(await reader.ServiceAsync(id, context.CancellationToken));
        visible = true;
        revokeOnStatus = false;
        revokeOnHistory = true;
        Assert.IsNull(await history.ReadAsync(id, now.AddHours(-1), now, context.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
