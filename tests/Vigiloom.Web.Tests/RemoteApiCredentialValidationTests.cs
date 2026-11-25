using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Identity;
using Vigiloom.Web.Authentication;
using Vigiloom.Infrastructure.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteApiCredentialValidationTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void Registration_ProvidesVerifierForAnotherService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Services:Identity:Url"] = "http://identity" }).Build());
        services.AddLogging();
        services.AddVigiloomIdentityCredentialClient();
        services.AddAuthentication().AddVigiloomApiCredentials();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsInstanceOfType<RemoteApiCredentialValidation>(scope.ServiceProvider.GetRequiredService<IApiCredentialValidation>());
    }

    [TestMethod]
    public async Task Verification_UsesIdentityServiceAndReflectsRevocation()
    {
        var id = Guid.CreateVersion7();
        var handler = new IdentityHandler(id);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity/") };
        var client = new RemoteApiCredentialValidation(http);
        var result = await client.VerifyAsync("example", CancellationToken.None);
        Assert.AreEqual(id, result!.Id);
        Assert.IsTrue(result.CanWrite);
        handler.Revoked = true;
        Assert.IsNull(await client.VerifyAsync("example", CancellationToken.None));
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task IdentityClient_ResolvesConfiguredServiceEndpoint()
    {
        var id = Guid.CreateVersion7();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var identity = builder.Build();
        identity.MapPost("/internal/identity/api-credentials/validate", () => Results.Json(new VerifiedApiCredentialResponse(id, "integration", true)));
        identity.MapPost("/internal/identity/cookies/validate", () => Results.Ok());
        await identity.StartAsync(TestContext.CancellationToken);
        var address = new Uri(identity.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:Identity:Url"] = "http://identity",
            ["Services:identity:http:0"] = address.Authority
        }).Build());
        services.AddLogging();
        services.AddVigiloomIdentityCredentialClient();
        services.AddVigiloomIdentityCookieClient();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<IApiCredentialValidation>().VerifyAsync("example", TestContext.CancellationToken);
        Assert.AreEqual(id, response!.Id);
        using var cookieClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IdentityCookieValidator));
        using var cookieResponse = await cookieClient.PostAsJsonAsync("internal/identity/cookies/validate", new { }, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, cookieResponse.StatusCode);
    }

    private sealed class IdentityHandler(Guid id) : HttpMessageHandler
    {
        public bool Revoked { get; set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/internal/identity/api-credentials/validate", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<VerifyApiCredentialRequest>(cancellationToken: cancellationToken);
            Assert.AreEqual("vlg_example", body!.Value);
            return Revoked ? new(HttpStatusCode.Unauthorized) : new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new VerifiedApiCredentialResponse(id, "integration", true))
            };
        }
    }
}
