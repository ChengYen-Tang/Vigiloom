using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Web.HostMonitoring;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteSecurityAccessTests
{
    [TestMethod]
    public async Task RemoteQueriesDoNotRequireLocalSecurityServices()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Security:Url"] = "http://security" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient("security", client => client.BaseAddress = new Uri("http://security/"))
            .ConfigurePrimaryHttpMessageHandler(() => new BadRequestHandler());
        services.AddScoped<DetectionAccess>();
        services.AddScoped<InventoryAccess>();
        services.AddScoped<VulnerabilityAccess>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var id = Guid.CreateVersion7();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = DateTimeOffset.UtcNow;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<DetectionAccess>().GetEventsAsync(id, start, end, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<InventoryAccess>().GetAsync(id, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<VulnerabilityAccess>().GetCurrentAsync(id, 1, null, null, CancellationToken.None));
    }

    private sealed class BadRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }
}
