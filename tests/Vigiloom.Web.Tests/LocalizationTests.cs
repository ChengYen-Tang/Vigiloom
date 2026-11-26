using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class LocalizationTests(TestContext context)
{
    [TestMethod]
    [DataRow("en", "en")]
    [DataRow("zh-Hant", "zh-Hant")]
    [DataRow("zh-Hans", "zh-Hans")]
    [DataRow("fr", "zh-Hant")]
    public async Task LoginPageUsesSupportedRequestCulture(string requested, string expected)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue(requested));
        using var response = await client.GetAsync("/account/login", context.CancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(context.CancellationToken);
        Assert.Contains($"<html lang=\"{expected}\">", html);
        Assert.Contains("autostart=\"false\"", html);
        Assert.Contains("culture-start", html);
    }
}
