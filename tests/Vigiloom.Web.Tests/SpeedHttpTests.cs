using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Web.Tests;
[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class SpeedHttpTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public Task ScheduleSettingsAreProtectedAndManualStartIsUnavailable() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync("/api/v1/speed-tests?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync("/api/v1/settings/speed-tests", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.PostAsJsonAsync("/api/v1/speed-tests", new { }, token)) Assert.AreEqual(HttpStatusCode.MethodNotAllowed, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var first = await admin.GetFromJsonAsync<SpeedSettingsSnapshot>("/api/v1/settings/speed-tests", token);
        Assert.IsNotNull(first); Assert.IsTrue(first.Automatic); Assert.IsNotNull(first.NextAutomaticAt);
        using (var csrf = await admin.PutAsJsonAsync("/api/v1/settings/speed-tests", new SpeedSettingsInput(false, 6, 2, 40, first.Version), token)) Assert.AreEqual(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var page = await admin.GetAsync("/settings/speed-tests", token);
        Assert.IsTrue(page.Headers.CacheControl?.NoStore);
        var form = await FormAsync(page, "speed-settings", token);
        var fields = Hidden(form);
        fields["Input.Hours"] = "6"; fields["Input.MaxMiB"] = "2"; fields["Input.Timeout"] = "40";
        fields["Input.Automatic"] = "false";
        using (var save = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已儲存", await TextAsync(save, token));
        using (var stale = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已被其他操作變更", await TextAsync(stale, token));
        var updated = await admin.GetFromJsonAsync<SpeedSettingsSnapshot>("/api/v1/settings/speed-tests", token);
        Assert.IsNotNull(updated); Assert.IsFalse(updated.Automatic); Assert.AreEqual(2, updated.MaxMiB);
        using var response = await admin.PostAsJsonAsync("/api/v1/speed-tests", new { }, token);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        using var invalidRange = await reader.GetAsync("/api/v1/speed-tests?from=2026-01-01T00:00:00Z&to=2026-11-01T00:00:00Z", token);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRange.StatusCode);
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task Details_ShowCoordinatesOnlyWhenProvided() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        var withCoordinates = new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow,
            State = SpeedTestState.Success, Latitude = 25.033, Longitude = 121.5654 };
        var missing = new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow, State = SpeedTestState.NoServer };
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            db.SpeedRuns.AddRange(withCoordinates, missing);
            await db.SaveChangesAsync(token);
        }
        using var reader = Client(factory);
        await LoginAsync(reader, "reader", token);
        using var populatedPage = await reader.GetAsync($"/speed-tests/{withCoordinates.Id:D}", token);
        Assert.AreEqual(HttpStatusCode.OK, populatedPage.StatusCode);
        Assert.Contains("25.033, 121.5654", await TextAsync(populatedPage, token));
        using var missingPage = await reader.GetAsync($"/speed-tests/{missing.Id:D}", token);
        Assert.AreEqual(HttpStatusCode.OK, missingPage.StatusCode);
        var html = await new HtmlParser().ParseDocumentAsync(await missingPage.Content.ReadAsStringAsync(token), token);
        var coordinate = html.QuerySelectorAll("dl div").Single(x => x.QuerySelector("dt")?.TextContent == "伺服器座標");
        Assert.AreEqual("目前無資料", coordinate.QuerySelector("dd")?.TextContent);
        var isp = html.QuerySelectorAll("dl div").Single(x => x.QuerySelector("dt")?.TextContent == "測速端 ISP");
        Assert.AreEqual("目前無資料", isp.QuerySelector("dd")?.TextContent);
        Assert.Contains("此測試未完整完成", html.Body!.TextContent);
        using var notFoundPage = await reader.GetAsync($"/speed-tests/{Guid.CreateVersion7():D}", token);
        Assert.AreEqual(HttpStatusCode.OK, notFoundPage.StatusCode);
        Assert.Contains("找不到測速結果", await TextAsync(notFoundPage, token));
    }, TestContext.CancellationToken);
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
    private static Dictionary<string,string> Hidden(IElement form) => form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    private static async Task<IElement> FormAsync(HttpResponseMessage response,string handler,CancellationToken token) =>
        (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token),token)).QuerySelectorAll("form").Single(x=>x.QuerySelector("input[name='_handler']")?.GetAttribute("value")==handler);
    private static async Task<string> TextAsync(HttpResponseMessage response,CancellationToken token) => (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token),token)).Body!.TextContent;
    private static async Task LoginAsync(HttpClient client,string name,CancellationToken token)
    {
        using var page=await client.GetAsync("/account/login",token); var fields=Hidden(await FormAsync(page,"login",token));
        fields["Input.UserName"]=name;fields["Input.Password"]="Strong-Password-123";
        using var response=await client.PostAsync("/account/login",new FormUrlEncodedContent(fields),token);Assert.AreEqual(HttpStatusCode.Redirect,response.StatusCode);
    }
}
