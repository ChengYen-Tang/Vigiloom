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
