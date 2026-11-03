using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class DeviceManagementTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task AdminCanCreateAndReaderCannotModify() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        const string secret = "test-only-bootstrap-secret-with-adequate-length";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = database.ConnectionString, ["Vigiloom:SetupToken"] = secret
            }));
        });
        var identity = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        await using (var db = await identity.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await RoleInitializer.InitializeAsync(identity, token);
        string readCredential;
        string writeCredential;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                .CreateAsync(secret, "admin", "Strong-Password-123", token)).Status);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var readerAccount = new Account { UserName = "reader" };
            Assert.IsTrue((await users.CreateAsync(readerAccount, "Reader-Password-123")).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(readerAccount, BuiltInRoles.ReadOnly)).Succeeded);
            var adminAccount = await users.FindByNameAsync("admin");
            Assert.IsNotNull(adminAccount);
            var bytes = RandomNumberGenerator.GetBytes(32);
            var readerKey = new ApiCredential("test reader", SHA256.HashData(bytes), false, DateTimeOffset.UtcNow.AddDays(1), adminAccount.Id);
            var writerKey = new ApiCredential("test writer", SHA256.HashData(bytes), true, DateTimeOffset.UtcNow.AddDays(1), adminAccount.Id);
            var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
            db.ApiCredentials.AddRange(readerKey, writerKey);
            await db.SaveChangesAsync(token);
            readCredential = $"vlg_{readerKey.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
            writeCredential = $"vlg_{writerKey.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
        }
        using var anonymous = Client(factory);
        using (var response = await anonymous.GetAsync("/api/v1/devices/", token)) Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
        using var admin = Client(factory);
        await LoginAsync(admin, "admin", "Strong-Password-123", token);
        using var page = await admin.GetAsync("/devices/new", token);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.IsTrue(page.Headers.CacheControl?.NoStore);
        var form = await FormAsync(page, "device-edit", token);
        var fields = Hidden(form);
        fields["Input.Name"] = "Test router";
        fields["Input.DeepMonitoring"] = "false";
        fields["Input.Enabled"] = "true";
        fields["Input.Address"] = "https://invalid.example";
        using (var invalid = await admin.PostAsync("/devices/new", new FormUrlEncodedContent(fields), token))
        {
            var invalidDocument = await new HtmlParser().ParseDocumentAsync(await invalid.Content.ReadAsStringAsync(token), token);
            Assert.Contains("請輸入有效的 IPv4、IPv6 或 DNS 名稱", invalidDocument.Body!.TextContent);
            Assert.AreEqual("Test router", invalidDocument.QuerySelector("#device-name")!.GetAttribute("value"));
            Assert.AreEqual("https://invalid.example", invalidDocument.QuerySelector("#device-address")!.GetAttribute("value"));
        }
        fields["Input.Address"] = "EXAMPLE.COM";
        using var create = await admin.PostAsync("/devices/new", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, create.StatusCode, (await new HtmlParser().ParseDocumentAsync(await create.Content.ReadAsStringAsync(token), token)).Body?.TextContent);
        using var list = await admin.GetAsync("/api/v1/devices/", token);
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
        var devices = await list.Content.ReadFromJsonAsync<DevicePage>(cancellationToken: token);
        Assert.IsNotNull(devices);
        Assert.AreEqual(1, devices.Total);
        Assert.AreEqual("example.com", devices.Items[0].Address);
        var now = DateTimeOffset.UtcNow;
        var auditUrl = $"/api/v1/operations/audit-feed/devices?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var auditFeed = await admin.GetFromJsonAsync<AuditFeedPage>(auditUrl, token);
        Assert.IsNotNull(auditFeed);
        Assert.IsTrue(auditFeed.Items.Any(item => item.SubjectId == devices.Items[0].Id && item.Action == AuditFeedAction.DeviceCreated));
        using var reader = Client(factory);
        await LoginAsync(reader, "reader", "Reader-Password-123", token);
        using (var response = await reader.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await reader.GetAsync("/devices/new", token)) Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await reader.GetAsync($"/devices/{devices.Items[0].Id:D}", token))
        {
            var content = (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("編輯設備", content);
            Assert.DoesNotContain("確認封存", content);
        }
        using (var response = await reader.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("No", "example.org", [], false), token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await admin.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("No CSRF", "example.org", [], false), token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using var apiReader = Client(factory);
        apiReader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readCredential);
        using (var response = await apiReader.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await apiReader.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("Denied", "example.org", [], false), token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        using var apiWriter = Client(factory);
        apiWriter.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writeCredential);
        using (var response = await apiWriter.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("Public", "example.org", [], false, Public: true), token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using (var response = await apiWriter.PostAsync("/api/v1/devices/", new StringContent("{\"name\":\"API device\",\"address\":\"example.org\",\"deepMonitoring\":true}", Encoding.UTF8, "application/json"), token))
        {
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            var apiDevice = await response.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token);
            Assert.IsNotNull(apiDevice);
            Assert.IsTrue(apiDevice.Enabled);
            var update = new DeviceInput("API device renamed", "example.org", [], true, Version: apiDevice.Version);
            using var updated = await apiWriter.PutAsJsonAsync($"/api/v1/devices/{apiDevice.Id:D}", update, token);
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            using var stale = await apiWriter.PutAsJsonAsync($"/api/v1/devices/{apiDevice.Id:D}", update, token);
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.AreEqual("application/problem+json", stale.Content.Headers.ContentType?.MediaType);
        }
        var detailUrl = $"/devices/{devices.Items[0].Id:D}";
        using (var detail = await admin.GetAsync(detailUrl, token))
        {
            var archive = Hidden(await FormAsync(detail, "device-archive", token));
            using var unconfirmed = await admin.PostAsync(detailUrl, new FormUrlEncodedContent(archive), token);
            Assert.Contains("請先確認操作影響", (await new HtmlParser().ParseDocumentAsync(await unconfirmed.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            archive["Action.Confirm"] = "true";
            using var archived = await admin.PostAsync(detailUrl, new FormUrlEncodedContent(archive), token);
            Assert.AreEqual(HttpStatusCode.Redirect, archived.StatusCode);
        }
        using (var archivedDetail = await admin.GetAsync(detailUrl, token))
        {
            Assert.Contains("已封存", (await new HtmlParser().ParseDocumentAsync(await archivedDetail.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            var restore = Hidden(await FormAsync(archivedDetail, "device-archive", token));
            restore["Action.Confirm"] = "true";
            using var restored = await admin.PostAsync(detailUrl, new FormUrlEncodedContent(restore), token);
            Assert.AreEqual(HttpStatusCode.Redirect, restored.StatusCode);
        }
        using (var restoredDetail = await admin.GetAsync(detailUrl, token)) Assert.Contains("監控已停用", (await new HtmlParser().ParseDocumentAsync(await restoredDetail.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
    }, TestContext.CancellationToken);

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
    private static Dictionary<string, string> Hidden(IElement form) => form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    private static async Task<IElement> FormAsync(HttpResponseMessage response, string name, CancellationToken token)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        return document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == name);
    }
    private static async Task LoginAsync(HttpClient client, string name, string password, CancellationToken token)
    {
        using var page = await client.GetAsync("/account/login", token);
        var fields = Hidden(await FormAsync(page, "login", token));
        fields["Input.UserName"] = name;
        fields["Input.Password"] = password;
        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
    }
}
