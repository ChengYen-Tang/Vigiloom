using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Vigiloom.Web.Tests;
[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class SpeedHttpTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public Task AdminConsentSettingsAndQueueAreProtected() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync("/api/v1/speed-tests?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync("/api/v1/settings/speed-tests", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.PostAsJsonAsync("/api/v1/speed-tests", new StartSpeedTestRequest(Guid.CreateVersion7()), token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var first = await admin.GetFromJsonAsync<SpeedSettingsSnapshot>("/api/v1/settings/speed-tests", token);
        Assert.IsNotNull(first); Assert.IsFalse(first.Consent); Assert.IsFalse(first.Automatic);
        using (var csrf = await admin.PutAsJsonAsync("/api/v1/settings/speed-tests", new SpeedSettingsInput(false, 6, 2, 5, true, first.Version, first.ConsentPolicy), token)) Assert.AreEqual(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var page = await admin.GetAsync("/settings/speed-tests", token);
        Assert.IsTrue(page.Headers.CacheControl?.NoStore);
        var form = await FormAsync(page, "speed-settings", token);
        var fields = Hidden(form);
        fields["Input.Hours"] = "6"; fields["Input.MaxMiB"] = "2"; fields["Input.Timeout"] = "5";
        fields["Input.Consent"] = "true"; fields["Input.Automatic"] = "false";
        using (var save = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已儲存", await TextAsync(save, token));
        using (var stale = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已被其他操作變更", await TextAsync(stale, token));
        var updated = await admin.GetFromJsonAsync<SpeedSettingsSnapshot>("/api/v1/settings/speed-tests", token);
        Assert.IsNotNull(updated); Assert.IsTrue(updated.Consent); Assert.AreEqual(2, updated.MaxMiB);
        var json = System.Text.Json.JsonSerializer.Serialize(new StartSpeedTestRequest(Guid.CreateVersion7()), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var start = new HttpRequestMessage(HttpMethod.Post, "/api/v1/speed-tests") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        start.Headers.Add("RequestVerificationToken", fields["__RequestVerificationToken"]);
        using var response = await admin.SendAsync(start, token);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<SpeedRequestResult>(cancellationToken: token);
        Assert.IsNotNull(queued); Assert.IsNotNull(queued.RunId);
        var detail = await reader.GetFromJsonAsync<SpeedRunDetails>($"/api/v1/speed-tests/{queued.RunId:D}", token);
        Assert.IsNotNull(detail); Assert.AreEqual(SpeedTestState.Queued, detail.Summary.State); Assert.IsNull(detail.Summary.DownloadMbps);
        using var invalidRange = await reader.GetAsync("/api/v1/speed-tests?from=2026-01-01T00:00:00Z&to=2026-11-01T00:00:00Z", token);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRange.StatusCode);
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
