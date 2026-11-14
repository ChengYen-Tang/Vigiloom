using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Api;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DeploymentLinkHttpTests(TestContext context)
{
    [TestMethod]
    public Task Relationships_RequireAdministratorAndStayPrivate() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var source = new ServiceSource("deployment source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "orders", "test", "Orders", now);
        var service = new MonitoredService(candidate, now);
        var privateCandidate = new ServiceCandidate(source.Id, "private-service", "test", "Sensitive private service", now);
        var privateService = new MonitoredService(privateCandidate, now);
        var instanceId = Guid.CreateVersion7();
        var privateInstanceId = Guid.CreateVersion7();
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.AddRange(candidate, privateCandidate); db.Services.AddRange(service, privateService); await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instanceId},{service.Id},{source.Id},{"node"},{now},{now})", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({privateInstanceId},{privateService.Id},{source.Id},{"private-node"},{now},{now})", token);
        }
        using var admin = PingTestSite.Client(host);
        using var reader = PingTestSite.Client(host);
        using var anonymous = PingTestSite.Client(host);
        using var apiClient = PingTestSite.Client(host);
        await PingTestSite.LoginAsync(admin, "admin", token);
        await PingTestSite.LoginAsync(reader, "reader", token);
        var secret = RandomNumberGenerator.GetBytes(32);
        await using (var identity = await host.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>().CreateDbContextAsync(token))
        {
            var actor = await identity.Users.Where(x => x.UserName == "admin").Select(x => x.Id).SingleAsync(token);
            var credential = new ApiCredential("test writer", SHA256.HashData(secret), true, now.AddDays(1), actor);
            identity.ApiCredentials.Add(credential);
            await identity.SaveChangesAsync(token);
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(secret)}");
        }
        var csrf = await admin.GetFromJsonAsync<AntiforgeryResponse>("/api/v1/session/antiforgery", token);
        Assert.IsNotNull(csrf);
        var path = $"/api/v1/service-details/{service.Id:D}/deployments";
        var input = new CreateDeploymentLink(device.Id, instanceId, "Deployment verified by administrator");
        using (var denied = await anonymous.GetAsync(path, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await reader.PostAsJsonAsync(path, input, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await apiClient.PostAsJsonAsync(path, input, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var noCsrf = await admin.PostAsJsonAsync(path, input, token)) Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        async Task<HttpResponseMessage> SendAsync(string url, object value)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(value) };
            request.Headers.Add("RequestVerificationToken", csrf.RequestToken);
            return await admin.SendAsync(request, token);
        }
        using (var created = await SendAsync(path, input)) Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        using (var privateCreated = await SendAsync($"/api/v1/service-details/{privateService.Id:D}/deployments", new CreateDeploymentLink(device.Id, privateInstanceId, "Private deployment")))
            Assert.AreEqual(HttpStatusCode.Created, privateCreated.StatusCode);
        using (var duplicate = await SendAsync(path, input)) Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        var page = await reader.GetFromJsonAsync<DeploymentLinkPage>(path, token);
        Assert.IsNotNull(page);
        var link = page.Items.Single();
        Assert.AreEqual(device.Id, link.DeviceId);
        Assert.AreEqual(instanceId, link.ServiceInstanceId);
        Assert.AreEqual(input.Evidence, link.Evidence);
        using (var wrongService = await SendAsync($"/api/v1/service-details/{privateService.Id:D}/deployments/{link.Id:D}/end", new EndDeploymentLink(link.Version)))
            Assert.AreEqual(HttpStatusCode.NotFound, wrongService.StatusCode);
        await using (var publishingScope = host.Services.CreateAsyncScope())
        {
            var publishing = publishingScope.ServiceProvider.GetRequiredService<PublicationAdministration>();
            var before = await publishing.ReadAsync(PublicationResourceKind.Service, service.Id, token);
            Assert.IsNotNull(before);
            var changed = await publishing.SetAsync(PublicationResourceKind.Service, service.Id,
                new PublicationChange(true, "Public orders", PublicationPolicy.ServiceHistoryDetailsVersion, before.Version), Guid.CreateVersion7(), token);
            Assert.AreEqual(PublicationChangeStatus.Updated, changed.Status);
        }
        var publicJson = await anonymous.GetStringAsync("/api/v1/public/services", token);
        Assert.Contains("Public orders", publicJson);
        Assert.DoesNotContain(device.Id.ToString("D"), publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(device.Name, publicJson);
        Assert.DoesNotContain(input.Evidence, publicJson);
        Assert.DoesNotContain(privateService.Id.ToString("D"), publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sensitive private service", publicJson);
        var settingsPath = $"/settings/service-deployments?service={service.Id:D}";
        using (var deniedPage = await reader.GetAsync(settingsPath, token))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, deniedPage.StatusCode);
            Assert.IsNotNull(deniedPage.Headers.Location);
            Assert.Contains("access-denied", deniedPage.Headers.Location.OriginalString, StringComparison.OrdinalIgnoreCase);
        }
        using var settings = await admin.GetAsync(settingsPath, token);
        Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
        var document = await new HtmlParser().ParseDocumentAsync(await settings.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "deployment-end");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
        fields["EndForm.Selection"] = $"{link.Id:D}|{link.Version}";
        using (var denied = await reader.PostAsJsonAsync($"{path}/{link.Id:D}/end", new EndDeploymentLink(link.Version), token))
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var ended = await admin.PostAsync(settingsPath, new FormUrlEncodedContent(fields), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, ended.StatusCode);
            var rendered = await new HtmlParser().ParseDocumentAsync(await ended.Content.ReadAsStringAsync(token), token);
            Assert.AreEqual("部署關聯已結束。", rendered.QuerySelector("main [role=status]")?.TextContent);
        }
        using (var stale = await SendAsync($"{path}/{link.Id:D}/end", new EndDeploymentLink(link.Version))) Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        page = await reader.GetFromJsonAsync<DeploymentLinkPage>(path, token);
        Assert.IsNotNull(page);
        Assert.IsEmpty(page.Items);
        using var emptySettings = await admin.GetAsync(settingsPath, token);
        var emptyDocument = await new HtmlParser().ParseDocumentAsync(await emptySettings.Content.ReadAsStringAsync(token), token);
        var createForm = emptyDocument.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "deployment-create");
        var createFields = createForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
        createFields["CreateForm.DeviceId"] = device.Id.ToString("D");
        createFields["CreateForm.InstanceId"] = instanceId.ToString("D");
        createFields["CreateForm.Evidence"] = "Verified by deployment operator";
        using var createdFromPage = await admin.PostAsync(settingsPath, new FormUrlEncodedContent(createFields), token);
        var createdDocument = await new HtmlParser().ParseDocumentAsync(await createdFromPage.Content.ReadAsStringAsync(token), token);
        Assert.AreEqual("部署關聯已建立。", createdDocument.QuerySelector("main [role=status]")?.TextContent);
        page = await reader.GetFromJsonAsync<DeploymentLinkPage>(path, token);
        Assert.IsNotNull(page);
        Assert.AreEqual("Verified by deployment operator", page.Items.Single().Evidence);
        using var scope = host.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<OperationAuditQueryService>().QueryAsync(
            new OperationAuditQuery(now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)), token);
        var deploymentAudits = audits.Items.Where(x => x.DeploymentLink is not null).ToArray();
        Assert.HasCount(4, deploymentAudits);
        Assert.AreEqual(3, deploymentAudits.Count(x => x.DeploymentLink!.Action == DeploymentLinkAction.Created));
        Assert.AreEqual(1, deploymentAudits.Count(x => x.DeploymentLink!.Action == DeploymentLinkAction.Ended));
    }, context.CancellationToken);
}
