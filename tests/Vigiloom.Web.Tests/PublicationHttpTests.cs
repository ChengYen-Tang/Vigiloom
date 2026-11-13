using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Api;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PublicationHttpTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PublicProjection_FiltersPrivateResourcesAndRevokesImmediately() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        var visible = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var hidden = new Device("Private server 10.1.2.3", "10.1.2.3", true, now);
        var group = new Group("Internal group", now);
        var emptyGroup = new Group("Secret only", now);
        Guid serviceId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            db.Devices.Add(hidden); db.Groups.AddRange(group, emptyGroup);
            db.DeviceGroups.AddRange(new DeviceGroup(group.Id, visible.Id), new DeviceGroup(group.Id, hidden.Id), new DeviceGroup(emptyGroup.Id, hidden.Id));
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            var source = new ServiceSource("internal-source", new byte[32], now);
            var candidate = new ServiceCandidate(source.Id, "private-service-key", "internal-environment", "Internal service", now);
            var service = new MonitoredService(candidate, now); serviceId = service.Id;
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.ServiceGroups.Add(new ServiceGroup(group.Id, serviceId)); await db.SaveChangesAsync(token); }

        using var anonymous = PingTestSite.Client(factory);
        using var admin = PingTestSite.Client(factory);
        using var reader = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(admin, "admin", token);
        await PingTestSite.LoginAsync(reader, "reader", token);
        using (var unauthorized = await anonymous.GetAsync($"/api/v1/admin/publications/devices/{visible.Id:D}", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using (var forbidden = await reader.GetAsync($"/api/v1/admin/publications/devices/{visible.Id:D}", token)) Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var csrf = (await admin.GetFromJsonAsync<AntiforgeryResponse>("/api/v1/session/antiforgery", token))!.RequestToken;
        Guid adminId; string writeCredential;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var account = await scope.ServiceProvider.GetRequiredService<UserManager<Account>>().FindByNameAsync("admin");
            Assert.IsNotNull(account); adminId = account.Id;
            var bytes = RandomNumberGenerator.GetBytes(32);
            var credential = new ApiCredential("writer", SHA256.HashData(bytes), true, now.AddDays(1), adminId);
            var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
            db.ApiCredentials.Add(credential); await db.SaveChangesAsync(token);
            writeCredential = $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
        }

        async Task<PublicationSettings> Settings(string kind, Guid id) =>
            (await admin.GetFromJsonAsync<PublicationSettings>($"/api/v1/admin/publications/{kind}/{id:D}", token))!;
        async Task<HttpResponseMessage> Put(string kind, Guid id, PublicationChange change, bool includeToken = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/publications/{kind}/{id:D}") { Content = JsonContent.Create(change) };
            if (includeToken) request.Headers.Add("RequestVerificationToken", csrf);
            using (request) return await admin.SendAsync(request, token);
        }
        async Task Publish(string kind, Guid id, string name)
        {
            var settings = await Settings(kind, id);
            using var response = await Put(kind, id, new(true, name, PublicationPolicy.BaselineVersion, settings.Version));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(token));
        }

        using (var apiClient = PingTestSite.Client(factory))
        {
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writeCredential);
            using var privateCreate = await apiClient.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("API private", "api.example", [], false), token);
            Assert.AreEqual(HttpStatusCode.Created, privateCreate.StatusCode, await privateCreate.Content.ReadAsStringAsync(token));
            using var publicCreate = await apiClient.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("API public", "api.example", [], false, Public: true,
                PublicDisplayName: "Public API device", PublicPolicyVersion: PublicationPolicy.BaselineVersion), token);
            Assert.AreEqual(HttpStatusCode.Forbidden, publicCreate.StatusCode);
        }

        var initial = await Settings("devices", visible.Id);
        Assert.IsFalse(initial.Public);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writeCredential);
        using (var noCsrf = await Put("devices", visible.Id, new(true, "Network", PublicationPolicy.BaselineVersion, initial.Version), false)) Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using (var mixedCreate = await admin.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("Mixed", "example.org", [], false, Public: true,
            PublicDisplayName: "Public device", PublicPolicyVersion: PublicationPolicy.BaselineVersion), token)) Assert.AreEqual(HttpStatusCode.BadRequest, mixedCreate.StatusCode);
        admin.DefaultRequestHeaders.Authorization = null;
        using (var invalid = await Put("devices", visible.Id, new(true, "router-127.1", PublicationPolicy.BaselineVersion, initial.Version))) Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var invalidPolicy = await Put("devices", visible.Id, new(true, "Network", "unknown", initial.Version))) Assert.AreEqual(HttpStatusCode.BadRequest, invalidPolicy.StatusCode);
        await Publish("devices", visible.Id, "External network");
        await Publish("services", serviceId, "Order service");
        await Publish("groups", group.Id, "Taipei");
        await Publish("groups", emptyGroup.Id, "Hidden members");

        using var devices = await anonymous.GetAsync("/api/v1/public/devices", token);
        Assert.AreEqual(HttpStatusCode.OK, devices.StatusCode);
        Assert.IsTrue(devices.Headers.CacheControl?.NoStore);
        var deviceJson = await devices.Content.ReadAsStringAsync(token);
        Assert.Contains("External network", deviceJson);
        Assert.DoesNotContain("127.0.0.1", deviceJson);
        Assert.DoesNotContain("10.1.2.3", deviceJson);
        Assert.DoesNotContain("Private server", deviceJson);
        using (var privateDevice = await anonymous.GetAsync($"/api/v1/public/devices/{hidden.Id:D}", token)) Assert.AreEqual(HttpStatusCode.NotFound, privateDevice.StatusCode);
        using (var nonexistent = await anonymous.GetAsync($"/api/v1/public/devices/{Guid.CreateVersion7():D}", token)) Assert.AreEqual(HttpStatusCode.NotFound, nonexistent.StatusCode);
        using var groups = await anonymous.GetAsync("/api/v1/public/groups", token);
        Assert.AreEqual(HttpStatusCode.OK, groups.StatusCode);
        var groupPage = await groups.Content.ReadFromJsonAsync<PublicPage<PublicGroup>>(cancellationToken: token);
        Assert.IsNotNull(groupPage);
        Assert.HasCount(1, groupPage.Items);
        Assert.AreEqual(1, groupPage.Items[0].DeviceCount);
        Assert.AreEqual(1, groupPage.Items[0].ServiceCount);
        var serviceJson = await anonymous.GetStringAsync("/api/v1/public/services", token);
        Assert.Contains("Order service", serviceJson);
        Assert.DoesNotContain("internal-environment", serviceJson);
        Assert.DoesNotContain("private-service-key", serviceJson);
        using (var filtered = await anonymous.GetAsync($"/api/v1/public/groups/{group.Id:D}/services", token))
        { Assert.AreEqual(HttpStatusCode.OK, filtered.StatusCode); Assert.Contains("Order service", await filtered.Content.ReadAsStringAsync(token)); }

        // Unknown policy identifiers must fail closed even under SQL Server's case-insensitive,
        // trailing-space-insensitive comparison rules.
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            foreach (var unknown in new[] { "BASELINE-V1", "baseline-v1 " })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [resources].[Devices] SET [PublicPolicyVersion] = {unknown} WHERE [Id] = {visible.Id}", token);
                using var unavailable = await anonymous.GetAsync($"/api/v1/public/devices/{visible.Id:D}", token);
                Assert.AreEqual(HttpStatusCode.NotFound, unavailable.StatusCode);
                var projectedGroup = await anonymous.GetFromJsonAsync<PublicGroup>($"/api/v1/public/groups/{group.Id:D}", token);
                Assert.IsNotNull(projectedGroup); Assert.AreEqual(0, projectedGroup.DeviceCount);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [resources].[Devices] SET [PublicPolicyVersion] = {PublicationPolicy.BaselineVersion} WHERE [Id] = {visible.Id}", token);
        }

        var current = await Settings("devices", visible.Id);
        using (var revoke = await Put("devices", visible.Id, new(false, null, null, current.Version))) Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
        using (var after = await anonymous.GetAsync($"/api/v1/public/devices/{visible.Id:D}", token))
        { Assert.AreEqual(HttpStatusCode.NotFound, after.StatusCode); Assert.IsTrue(after.Headers.CacheControl?.NoStore); }
        var afterGroup = await anonymous.GetFromJsonAsync<PublicGroup>($"/api/v1/public/groups/{group.Id:D}", token);
        Assert.IsNotNull(afterGroup); Assert.AreEqual(0, afterGroup.DeviceCount); Assert.AreEqual(1, afterGroup.ServiceCount);
        var auditUrl = $"/api/v1/operations/audit/publications?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var audit = await admin.GetFromJsonAsync<OperationAuditPage>(auditUrl, token);
        Assert.IsNotNull(audit); Assert.IsTrue(audit.Items.Any(x => x.ResourceId == visible.Id && x.Publication is { CurrentPublic: false }));
        Assert.DoesNotContain("External network", JsonSerializer.Serialize(audit));

        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writeCredential);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/") { Content = JsonContent.Create(new DeviceInput("New internal", "new.example", [], false, Public: true,
            PublicDisplayName: "New public", PublicPolicyVersion: PublicationPolicy.BaselineVersion)) };
        create.Headers.Add("RequestVerificationToken", csrf);
        using var created = await admin.SendAsync(create, token);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(token));
        var createdDevice = await created.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token);
        Assert.IsNotNull(createdDevice);
        var createdAudit = await admin.GetFromJsonAsync<OperationAuditPage>(auditUrl, token);
        Assert.IsNotNull(createdAudit);
        Assert.IsTrue(createdAudit.Items.Any(x => x.ResourceId == createdDevice.Id && x.ActorId == adminId && x.Publication is { CurrentPublic: true }));
    }, context.CancellationToken);
}
