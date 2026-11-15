using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class MicrosoftCatalogRelationsTests(TestContext context)
{
    [TestMethod]
    [DataRow("2023-10 Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5031364)", true)]
    [DataRow("2023-10 Cumulative Update for Microsoft server operating system, version 22H2 for x64-based Systems (KB5031364)", false)]
    [DataRow("2023-10 Cumulative Update for Microsoft server operating system version 21H2 for ARM64-based Systems (KB5031364)", false)]
    [DataRow("Security Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5031364)", false)]
    [DataRow("2023-10 Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5031364) extra", false)]
    public void TryTitleKb_RequiresTheSupportedProductArchitectureAndExactKb(string title, bool expected)
    {
        Assert.AreEqual(expected, MicrosoftCatalogRelations.TryTitleKb(title, out var kb));
        if (expected) Assert.AreEqual("5031364", kb);
    }

    [TestMethod]
    [DataRow("Windows Server 2016", "2025-11 Cumulative Update for Windows Server 2016 for x64-based Systems (KB5068864)", "5068864")]
    [DataRow("Windows Server 2019", "2025-11 Cumulative Update for Windows Server 2019 for x64-based Systems (KB5068791)", "5068791")]
    [DataRow("Windows Server 2025", "2025-11 Cumulative Update for Microsoft server operating system version 24H2 for x64-based Systems (KB5068861) (26100.7171)", "5068861")]
    public void TryTitleKb_UsesTheCatalogTitleForEachProduct(string product, string title, string expected)
    {
        var scope = MicrosoftCatalogRelations.Scope(product);
        Assert.IsNotNull(scope);
        Assert.IsTrue(MicrosoftCatalogRelations.TryTitleKb(title, scope.Value.TitleProduct, out var kb));
        Assert.AreEqual(expected, kb);
        Assert.IsFalse(MicrosoftCatalogRelations.TryTitleKb(title.Replace("x64-based", "ARM64-based", StringComparison.Ordinal), scope.Value.TitleProduct, out _));
    }

    [TestMethod]
    public async Task HostGuard_RejectsOtherDestinations()
    {
        using var guard = new MicrosoftCatalogHostGuard { InnerHandler = new FakeHandler() };
        using var client = new HttpClient(guard);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.com/Search.aspx", context.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://www.catalog.update.microsoft.com/Search.aspx", context.CancellationToken));
        using var response = await client.GetAsync("https://www.catalog.update.microsoft.com/Search.aspx?q=KB5031364", context.CancellationToken);
        Assert.IsTrue(response.IsSuccessStatusCode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
