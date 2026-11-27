using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Web.HostMonitoring;
using Vigiloom.Web.AgentVersions;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteSecurityAccessTests
{
    [TestMethod]
    public async Task RemoteQueriesDoNotRequireLocalSecurityServices()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Security:Url"] = "http://security", ["Services:Security:IntelligenceEnabled"] = "true" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient("security", client => client.BaseAddress = new Uri("http://security/"))
            .ConfigurePrimaryHttpMessageHandler(() => new BadRequestHandler());
        services.AddScoped<DetectionAccess>();
        services.AddScoped<InventoryAccess>();
        services.AddScoped<VulnerabilityAccess>();
        services.AddScoped<IntegrityAccess>();
        services.AddScoped<IntegritySettingsAccess>();
        services.AddScoped<InventorySettingsAccess>();
        services.AddScoped<BaselineAccess>();
        services.AddScoped<IntelligenceStatusAccess>();
        services.AddScoped<IntelligencePublicationAccess>();
        services.AddScoped<IntelligenceImportAccess>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var id = Guid.CreateVersion7();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = DateTimeOffset.UtcNow;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<DetectionAccess>().GetEventsAsync(id, start, end, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<InventoryAccess>().GetAsync(id, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<VulnerabilityAccess>().GetCurrentAsync(id, 1, null, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IntegrityAccess>().GetAsync(id, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => scope.ServiceProvider.GetRequiredService<IntegritySettingsAccess>().ListAsync(id, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => scope.ServiceProvider.GetRequiredService<InventorySettingsAccess>().GetAsync(id, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => scope.ServiceProvider.GetRequiredService<BaselineAccess>().GetAsync(id, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IntelligenceStatusAccess>().GetSourcesAsync(1, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IntelligencePublicationAccess>().ActivateAsync(id, id, null, "", false, id, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => scope.ServiceProvider.GetRequiredService<IntelligenceImportAccess>().ImportAsync(id, new MemoryStream(), id, CancellationToken.None));
    }

    private sealed class BadRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }
}
