using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemplateReaderHttpTests(TestContext context)
{
    [TestMethod]
    public Task TemplateListAndImpact_RequireAdministratorAndShowAssignedDevices() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
            var now = DateTimeOffset.UtcNow;
            var template = new PublicationTemplate("Network", true, false, now);
            template.SetDefault(true, now);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            {
                db.Templates.Add(template);
                await db.SaveChangesAsync(token);
            }
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                row.SetTemplatePublication("Network link", template.Id, false, now);
                await db.SaveChangesAsync(token);
            }
            using var anonymous = PingTestSite.Client(host);
            using var reader = PingTestSite.Client(host);
            using var admin = PingTestSite.Client(host);
            await PingTestSite.LoginAsync(reader, "reader", token);
            await PingTestSite.LoginAsync(admin, "admin", token);
            var url = $"/api/v1/admin/publications/templates/{template.Id:D}/impact";
            const string listUrl = "/api/v1/admin/publications/templates";
            using (var response = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            using (var response = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await anonymous.GetAsync(listUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            using (var response = await reader.GetAsync(listUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            var list = await admin.GetFromJsonAsync<PublicationTemplatePage>(listUrl, token);
            Assert.IsNotNull(list);
            Assert.HasCount(1, list.Items);
            Assert.AreEqual(template.Id, list.Items[0].Id);
            Assert.IsTrue(list.Items[0].IsDefault);
            using var preview = await admin.GetAsync(url, token);
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
            Assert.IsTrue(preview.Headers.CacheControl?.NoStore);
            var impact = await preview.Content.ReadFromJsonAsync<PublicationImpact>(cancellationToken: token);
            Assert.IsNotNull(impact);
            Assert.AreEqual(1, impact.Count);
            Assert.AreEqual(1, impact.PublicCount);
            Assert.AreEqual(device.Id, impact.Items[0].Id);
            using var invalidPage = await admin.GetAsync(url + "?page=201", token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            {
                var retired = await db.Templates.SingleAsync(x => x.Id == template.Id, token);
                retired.Retire(now.AddMinutes(1));
                await db.SaveChangesAsync(token);
            }
            using var retiredResponse = await admin.GetAsync(url, token);
            Assert.AreEqual(HttpStatusCode.NotFound, retiredResponse.StatusCode);
            var create = new CreatePublicationTemplate("New template", false, true);
            using (var response = await anonymous.PostAsJsonAsync(listUrl, create, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            using (var response = await reader.PostAsJsonAsync(listUrl, create, token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await admin.PostAsJsonAsync(listUrl, create, token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var antiforgery = await admin.GetFromJsonAsync<JsonElement>("/api/v1/session/antiforgery", token);
            admin.DefaultRequestHeaders.Add("RequestVerificationToken", antiforgery.GetProperty("requestToken").GetString());
            using var created = await admin.PostAsJsonAsync(listUrl, create, token);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
            var createdTemplate = await created.Content.ReadFromJsonAsync<PublicationTemplateItem>(cancellationToken: token);
            Assert.IsNotNull(createdTemplate);
            Assert.AreEqual("New template", createdTemplate.Name);
            var defaultUrl = $"{listUrl}/{createdTemplate.Id:D}/default";
            var choice = new SetDefaultPublicationTemplate(createdTemplate.Version);
            using (var response = await reader.PutAsJsonAsync(defaultUrl, choice, token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            using var selectDefault = await admin.PutAsJsonAsync(defaultUrl, choice, token);
            Assert.AreEqual(HttpStatusCode.OK, selectDefault.StatusCode);
            var selected = await selectDefault.Content.ReadFromJsonAsync<PublicationTemplateItem>(cancellationToken: token);
            Assert.IsNotNull(selected);
            Assert.IsTrue(selected.IsDefault);
            var currentDefault = await admin.GetFromJsonAsync<PublicationTemplateItem>(listUrl + "/default", token);
            Assert.IsNotNull(currentDefault);
            Assert.AreEqual(createdTemplate.Id, currentDefault.Id);
            Assert.AreEqual(selected.Version, currentDefault.Version);
        }, context.CancellationToken);
}
