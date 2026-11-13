using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HealthTargetHttpTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task TargetSettings_RequireAdministratorAndAntiforgery_AndAuditChanges() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            var otherSource = new ServiceSource("other source", new byte[32], now);
            var otherService = new MonitoredService(new ServiceCandidate(otherSource.Id, "orders", "prod", "Orders", now), now);
            var instanceId = Guid.CreateVersion7();
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(source, otherSource); db.Services.AddRange(service, otherService); await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instanceId},{service.Id},{source.Id},{"orders-instance-1"},{now},{now})", token);
            }
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var anonymous = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            using var admin = factory.CreateClient(options);
            await LoginAsync(reader, "reader", token);
            await LoginAsync(admin, "admin", token);
            var url = $"/api/v1/service-health/targets/?serviceId={service.Id:D}";
            using (var denied = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            var statusUrl = $"/api/v1/service-health/{service.Id:D}/status";
            using (var denied = await anonymous.GetAsync(statusUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await reader.GetAsync("/settings/service-health", token)) Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            using (var denied = await anonymous.GetAsync("/api/v1/service-health/networks/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync("/api/v1/service-health/networks/history", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            var input = new HealthTargetInput("https://example.test/health?tag=ready", 30, 5, 3, 2, true, "");
            var create = new CreateHealthTargetInput(service.Id, null, input);
            using (var denied = await reader.PostAsJsonAsync("/api/v1/service-health/targets/", create, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await admin.PostAsJsonAsync("/api/v1/service-health/targets/", create, token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            var policy = await admin.GetFromJsonAsync<HealthNetworkPolicySnapshot>("/api/v1/service-health/networks/", token);
            Assert.IsNotNull(policy);
            Assert.AreEqual(0L, policy.Generation);
            using (var savedPolicy = await admin.PutAsJsonAsync("/api/v1/service-health/networks/", new HealthNetworkPolicyInput(["10.0.0.0/8"], policy.Version), token)) Assert.AreEqual(HttpStatusCode.OK, savedPolicy.StatusCode);
            using (var stalePolicy = await admin.PutAsJsonAsync("/api/v1/service-health/networks/", new HealthNetworkPolicyInput([], policy.Version), token)) Assert.AreEqual(HttpStatusCode.Conflict, stalePolicy.StatusCode);
            var revisions = await admin.GetFromJsonAsync<HealthNetworkRevisionSnapshot[]>("/api/v1/service-health/networks/history", token);
            Assert.IsNotNull(revisions);
            Assert.HasCount(1, revisions);
            Assert.Contains("10.0.0.0/8", revisions[0].Networks);
            var pageUrl = $"/settings/service-health?service={service.Id:D}";
            using (var page = await admin.GetAsync(pageUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                var text = (await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
                Assert.Contains("other source", text);
                Assert.Contains("source", text);
                Assert.Contains(otherSource.Id.ToString(), text);
                Assert.Contains(source.Id.ToString(), text);
                var fields = await HiddenFieldsAsync(page, "health-networks", token);
                fields["NetworkForm.Text"] = "10.0.0.0/8\n127.0.0.0/8";
                using var saved = await admin.PostAsync(pageUrl, new FormUrlEncodedContent(fields), token);
                Assert.Contains("網段設定已儲存", (await new HtmlParser().ParseDocumentAsync(await saved.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            }
            var changedPolicy = await admin.GetFromJsonAsync<HealthNetworkPolicySnapshot>("/api/v1/service-health/networks/", token);
            Assert.IsNotNull(changedPolicy);
            Assert.Contains("127.0.0.0/8", changedPolicy.Networks);
            using (var page = await admin.GetAsync(pageUrl, token))
            {
                var fields = await HiddenFieldsAsync(page, "health-target", token);
                fields["TargetForm.Endpoint"] = "https://ui.example.test/health";
                fields["TargetForm.IntervalSeconds"] = "30";
                fields["TargetForm.TimeoutSeconds"] = "5";
                fields["TargetForm.FailureThreshold"] = "3";
                fields["TargetForm.RecoveryThreshold"] = "2";
                fields["TargetForm.Enabled"] = "true";
                using var saved = await admin.PostAsync(pageUrl, new FormUrlEncodedContent(fields), token);
                Assert.Contains("健康檢查設定已儲存", (await new HtmlParser().ParseDocumentAsync(await saved.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            }
            HealthTargetSnapshot created;
            using (var saved = await admin.PostAsJsonAsync("/api/v1/service-health/targets/", create, token))
            {
                Assert.AreEqual(HttpStatusCode.Created, saved.StatusCode);
                Assert.IsNotNull(saved.Headers.Location);
                created = (await saved.Content.ReadFromJsonAsync<HealthTargetSnapshot>(cancellationToken: token))!;
            }
            HealthTargetSnapshot instanceTarget;
            using (var saved = await admin.PostAsJsonAsync("/api/v1/service-health/targets/", new CreateHealthTargetInput(service.Id, instanceId, input), token))
            {
                Assert.AreEqual(HttpStatusCode.Created, saved.StatusCode);
                instanceTarget = (await saved.Content.ReadFromJsonAsync<HealthTargetSnapshot>(cancellationToken: token))!;
            }
            var listed = await admin.GetFromJsonAsync<HealthTargetSnapshot[]>(url, token);
            Assert.IsNotNull(listed);
            Assert.HasCount(3, listed);
            using (var statusResponse = await reader.GetAsync(statusUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode);
                Assert.IsTrue(statusResponse.Headers.CacheControl?.NoStore);
                var health = await statusResponse.Content.ReadFromJsonAsync<ServiceHealthStatus>(cancellationToken: token);
                Assert.AreEqual(new ServiceHealthStatus(ServiceHealthState.Unknown, 3, 3, ServiceHealthUnknownReason.Invalidated), health);
            }
            var historyUrl = $"/api/v1/service-health/{service.Id:D}/history?from={Uri.EscapeDataString(now.AddMinutes(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"))}";
            using (var denied = await anonymous.GetAsync(historyUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var historyResponse = await reader.GetAsync(historyUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, historyResponse.StatusCode);
                Assert.IsTrue(historyResponse.Headers.CacheControl?.NoStore);
                var history = await historyResponse.Content.ReadFromJsonAsync<ServiceHealthHistory>(cancellationToken: token);
                Assert.IsNotNull(history);
                Assert.IsNull(history.Availability);
                Assert.AreEqual(0d, history.Coverage);
            }
            Assert.Contains(created.Id, listed.Select(x => x.Id));
            var editUrl = $"{pageUrl}&target={instanceTarget.Id:D}";
            using (var page = await admin.GetAsync(editUrl, token))
            {
                var text = (await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
                Assert.Contains("orders-instance-1", text);
                Assert.Contains(instanceId.ToString(), text);
                var fields = await HiddenFieldsAsync(page, "health-target", token);
                fields["TargetForm.Endpoint"] = "https://example.test/updated";
                fields["TargetForm.IntervalSeconds"] = "60";
                fields["TargetForm.TimeoutSeconds"] = "5";
                fields["TargetForm.FailureThreshold"] = "3";
                fields["TargetForm.RecoveryThreshold"] = "2";
                fields["TargetForm.Enabled"] = "true";
                using var saved = await admin.PostAsync(editUrl, new FormUrlEncodedContent(fields), token);
                Assert.Contains("健康檢查設定已儲存", (await new HtmlParser().ParseDocumentAsync(await saved.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
                using var stale = await admin.PostAsync(editUrl, new FormUrlEncodedContent(fields), token);
                Assert.Contains("設定已被修改", (await new HtmlParser().ParseDocumentAsync(await stale.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            }
            var editedInstance = await admin.GetFromJsonAsync<HealthTargetSnapshot>($"/api/v1/service-health/targets/{instanceTarget.Id:D}", token);
            Assert.IsNotNull(editedInstance);
            Assert.AreEqual(instanceId, editedInstance.InstanceId);
            var update = input with { IntervalSeconds = 60, Version = created.Version };
            using (var denied = await reader.PutAsJsonAsync($"/api/v1/service-health/targets/{created.Id:D}", update, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            var antiToken = admin.DefaultRequestHeaders.GetValues("RequestVerificationToken").Single();
            admin.DefaultRequestHeaders.Remove("RequestVerificationToken");
            using (var denied = await admin.PutAsJsonAsync($"/api/v1/service-health/targets/{created.Id:D}", update, token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            admin.DefaultRequestHeaders.Add("RequestVerificationToken", antiToken);
            using (var saved = await admin.PutAsJsonAsync($"/api/v1/service-health/targets/{created.Id:D}", update, token)) Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            using (var stale = await admin.PutAsJsonAsync($"/api/v1/service-health/targets/{created.Id:D}", update, token)) Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            var auditUrl = $"/api/v1/operations/audit/health-targets?from={Uri.EscapeDataString(now.AddMinutes(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}";
            var audit = await admin.GetFromJsonAsync<OperationAuditPage>(auditUrl, token);
            Assert.IsNotNull(audit);
            Assert.HasCount(5, audit.Items);
            Assert.IsTrue(audit.Items.All(x => x.HealthTarget?.ServiceId == service.Id));
            var auditJson = JsonSerializer.Serialize(audit);
            Assert.DoesNotContain("example.test", auditJson);
            Assert.DoesNotContain("tag=ready", auditJson);
            using var auditPage = await admin.GetAsync("/settings/audit?source=health-targets", token);
            var auditHtml = await auditPage.Content.ReadAsStringAsync(token);
            var document = await new HtmlParser().ParseDocumentAsync(auditHtml, token);
            Assert.Contains("新增檢查", document.Body!.TextContent);
            Assert.Contains("更新檢查", document.Body.TextContent);
            Assert.DoesNotContain("example.test", document.Body.TextContent);
        }, context.CancellationToken);

    private static async Task LoginAsync(HttpClient client, string username, CancellationToken token)
    {
        using var page = await client.GetAsync("/account/login", token);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
        fields["Input.UserName"] = username;
        fields["Input.Password"] = "Strong-Password-123";
        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<Dictionary<string, string>> HiddenFieldsAsync(HttpResponseMessage response, string formName, CancellationToken token)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == formName);
        return form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    }
}
