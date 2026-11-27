using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vigiloom.Contracts.Identity;
using Vigiloom.Infrastructure.Authentication;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
public sealed class ForwardedAuthenticationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(true, HttpStatusCode.BadRequest)]
    [DataRow(false, HttpStatusCode.Unauthorized)]
    public async Task OnlyTrustedProxyCanForwardHttpsForApiCredentials(bool trusted, HttpStatusCode expected)
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseUrls("http://127.0.0.1:0");
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Resources"] = "Server=127.0.0.1;Database=test;User ID=test;Password=test;TrustServerCertificate=True",
                ["Vigiloom:Gateway:TrustedProxies:0"] = trusted ? "127.0.0.1" : "192.0.2.1"
            }));
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IApiCredentialValidation>();
                services.AddSingleton<IApiCredentialValidation, AllowCredential>();
            });
        });
        app.UseKestrel(0);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-packages?page=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_test");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request, TestContext.CancellationToken);
        Assert.AreEqual(expected, response.StatusCode);
    }

    private sealed class AllowCredential : IApiCredentialValidation
    {
        public Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token) =>
            Task.FromResult<VerifiedApiCredentialResponse?>(new(Guid.CreateVersion7(), "test", false));
    }
}
