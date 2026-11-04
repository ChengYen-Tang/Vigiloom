using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class GroupManagementTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task GroupMembershipAuthorizationConcurrencyAndDeletion() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
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
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        var serviceFactory = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        await using (var db = await serviceFactory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        Guid serviceId;
        await using (var db = await serviceFactory.CreateDbContextAsync(token))
        {
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("test source", new byte[32], now);
            var candidate = new ServiceCandidate(source.Id, "orders", "production", "Orders", now);
            var monitored = new MonitoredService(candidate, now);
            db.Sources.Add(source);
            db.Candidates.Add(candidate);
            db.Services.Add(monitored);
            await db.SaveChangesAsync(token);
            serviceId = monitored.Id;
        }
        await RoleInitializer.InitializeAsync(identity, token);
        string readerKey, writerKey;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                .CreateAsync(secret, "admin", "Strong-Password-123", token)).Status);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var readerAccount = new Account { UserName = "reader" };
            Assert.IsTrue((await users.CreateAsync(readerAccount, "Reader-Password-123")).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(readerAccount, BuiltInRoles.ReadOnly)).Succeeded);
            var admin = await users.FindByNameAsync("admin");
            Assert.IsNotNull(admin);
            var bytes = RandomNumberGenerator.GetBytes(32);
            var read = new ApiCredential("group reader", SHA256.HashData(bytes), false, DateTimeOffset.UtcNow.AddDays(1), admin.Id);
            var write = new ApiCredential("group writer", SHA256.HashData(bytes), true, DateTimeOffset.UtcNow.AddDays(1), admin.Id);
            var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
            db.ApiCredentials.AddRange(read, write);
            await db.SaveChangesAsync(token);
            readerKey = $"vlg_{read.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
            writerKey = $"vlg_{write.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
        }
        using var anonymous = Client(factory);
        using var reader = Client(factory);
        using var writer = Client(factory);
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readerKey);
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writerKey);
        using (var response = await anonymous.GetAsync("/api/v1/groups/", token)) Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await reader.PostAsJsonAsync("/api/v1/groups/", new GroupInput("No", null), token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        using var deviceResponse = await writer.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("Router", "router.example", [], false), token);
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token);
        Assert.IsNotNull(device);
        using var create = await writer.PostAsJsonAsync("/api/v1/groups/", new GroupInput(" Office ", " Test "), token);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        var group = await create.Content.ReadFromJsonAsync<GroupDetails>(cancellationToken: token);
        Assert.IsNotNull(group);
        Assert.AreEqual("Office", group.Name);
        Assert.AreEqual("Test", group.Description);
        using (var duplicate = await writer.PostAsJsonAsync("/api/v1/groups/", new GroupInput("Office", null), token)) Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using (var missing = await writer.PutAsJsonAsync($"/api/v1/groups/{group.Id:D}/devices", new GroupMembershipChange(group.Version, [Guid.CreateVersion7()], []), token))
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        using var add = await writer.PutAsJsonAsync($"/api/v1/groups/{group.Id:D}/devices", new GroupMembershipChange(group.Version, [device.Id, device.Id], []), token);
        Assert.AreEqual(HttpStatusCode.OK, add.StatusCode, await add.Content.ReadAsStringAsync(token));
        var changed = await add.Content.ReadFromJsonAsync<GroupDetails>(cancellationToken: token);
        Assert.IsNotNull(changed);
        Assert.AreEqual(1, changed.DeviceCount);
        using (var addService = await writer.PutAsJsonAsync($"/api/v1/groups/{group.Id:D}/services", new GroupMembershipChange(changed.Version, [serviceId], []), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, addService.StatusCode, await addService.Content.ReadAsStringAsync(token));
            changed = await addService.Content.ReadFromJsonAsync<GroupDetails>(cancellationToken: token);
            Assert.IsNotNull(changed);
        }
        var serviceMembers = await reader.GetFromJsonAsync<GroupServicePage>($"/api/v1/groups/{group.Id:D}/services?member=true&search=Orders", token);
        Assert.IsNotNull(serviceMembers);
        Assert.AreEqual(1, serviceMembers.Total);
        Assert.AreEqual(serviceId, serviceMembers.Items[0].Id);
        Assert.IsTrue(serviceMembers.Items[0].IsMember);
        Assert.AreEqual(1, changed.ServiceCount);
        using (var forbiddenService = await reader.PutAsJsonAsync($"/api/v1/groups/{group.Id:D}/services", new GroupMembershipChange(changed.Version, [], [serviceId]), token))
            Assert.AreEqual(HttpStatusCode.Forbidden, forbiddenService.StatusCode);
        using (var staleService = await writer.PutAsJsonAsync($"/api/v1/groups/{group.Id:D}/services", new GroupMembershipChange(group.Version, [], [serviceId]), token))
            Assert.AreEqual(HttpStatusCode.Conflict, staleService.StatusCode);
        using (var stale = await writer.PutAsJsonAsync($"/api/v1/groups/{group.Id:D}/devices", new GroupMembershipChange(group.Version, [], [device.Id]), token)) Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        using var group2Response = await writer.PostAsJsonAsync("/api/v1/groups/", new GroupInput("Systems", null), token);
        var group2 = await group2Response.Content.ReadFromJsonAsync<GroupDetails>(cancellationToken: token);
        Assert.IsNotNull(group2);
        using (var addSecond = await writer.PutAsJsonAsync($"/api/v1/groups/{group2.Id:D}/devices", new GroupMembershipChange(group2.Version, [device.Id], []), token)) Assert.AreEqual(HttpStatusCode.OK, addSecond.StatusCode);
        var ungrouped = await reader.GetFromJsonAsync<GroupDevicePage>("/api/v1/groups/ungrouped-devices", token);
        Assert.IsNotNull(ungrouped);
        Assert.AreEqual(0, ungrouped.Total);
        using var adminClient = Client(factory);
        await LoginAsync(adminClient, "admin", "Strong-Password-123", token);
        using (var noCsrf = await adminClient.PostAsJsonAsync("/api/v1/groups/", new GroupInput("No CSRF", null), token)) Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using (var available = await adminClient.GetAsync($"/groups/{group2.Id:D}?serviceFilter=available", token))
        {
            var serviceForm = await FormAsync(available, "group-services", token);
            Assert.IsNotNull(serviceForm.QuerySelector("button[value='add']"));
            Assert.IsNull(serviceForm.QuerySelector("button[value='remove']"));
            var serviceFields = Hidden(serviceForm);
            serviceFields["serviceIds"] = serviceId.ToString();
            serviceFields["serviceOperation"] = "add";
            using var addedByPage = await adminClient.PostAsync($"/groups/{group2.Id:D}?serviceFilter=available", new FormUrlEncodedContent(serviceFields), token);
            Assert.AreEqual(HttpStatusCode.Redirect, addedByPage.StatusCode, await addedByPage.Content.ReadAsStringAsync(token));
        }
        Dictionary<string, string> staleServiceFields;
        using (var membersPage = await adminClient.GetAsync($"/groups/{group2.Id:D}", token))
        {
            var memberForm = await FormAsync(membersPage, "group-services", token);
            Assert.IsNull(memberForm.QuerySelector("button[value='add']"));
            Assert.IsNotNull(memberForm.QuerySelector("button[value='remove']"));
            var memberFields = Hidden(memberForm);
            memberFields["serviceIds"] = serviceId.ToString();
            memberFields["serviceOperation"] = "remove";
            staleServiceFields = new(memberFields);
            using var removedByPage = await adminClient.PostAsync($"/groups/{group2.Id:D}", new FormUrlEncodedContent(memberFields), token);
            Assert.AreEqual(HttpStatusCode.Redirect, removedByPage.StatusCode, await removedByPage.Content.ReadAsStringAsync(token));
        }
        using (var stalePage = await adminClient.PostAsync($"/groups/{group2.Id:D}", new FormUrlEncodedContent(staleServiceFields), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, stalePage.StatusCode);
            Assert.Contains("群組已變更", WebUtility.HtmlDecode(await stalePage.Content.ReadAsStringAsync(token)));
            var deleteFields = Hidden(await FormAsync(stalePage, "group-delete", token));
            Assert.IsFalse(string.IsNullOrEmpty(deleteFields["Deletion.Version"]));
        }
        var ungroupedAfterRemoval = await reader.GetFromJsonAsync<GroupServicePage>("/api/v1/groups/ungrouped-services", token);
        Assert.IsNotNull(ungroupedAfterRemoval);
        Assert.AreEqual(0, ungroupedAfterRemoval.Total); // Still a member of the first group.
        using var groupPage = await adminClient.GetAsync($"/groups/{group.Id:D}?filter=all", token);
        Assert.AreEqual(HttpStatusCode.OK, groupPage.StatusCode);
        Assert.Contains("Orders", await groupPage.Content.ReadAsStringAsync(token));
        using (var filtered = await adminClient.GetAsync($"/groups/{group.Id:D}?search=Router&filter=all&page=2&serviceSearch=Orders&serviceFilter=all&servicePage=3", token))
        {
            var document = await new HtmlParser().ParseDocumentAsync(await filtered.Content.ReadAsStringAsync(token), token);
            var devicePrevious = document.QuerySelector("nav[aria-label='設備分頁'] a")?.GetAttribute("href");
            var servicePrevious = document.QuerySelector("nav[aria-label='服務分頁'] a")?.GetAttribute("href");
            Assert.IsNotNull(devicePrevious);
            Assert.IsNotNull(servicePrevious);
            var deviceQuery = QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), devicePrevious).Query);
            var serviceQuery = QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), servicePrevious).Query);
            Assert.AreEqual("Orders", deviceQuery["serviceSearch"].ToString());
            Assert.AreEqual("3", deviceQuery["servicePage"].ToString());
            Assert.AreEqual("Router", serviceQuery["search"].ToString());
            Assert.AreEqual("2", serviceQuery["page"].ToString());
        }
        var form = await FormAsync(groupPage, "group-delete", token);
        var fields = Hidden(form);
        fields["Deletion.Confirm"] = "true";
        using var deleted = await adminClient.PostAsync($"/groups/{group.Id:D}", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(token));
        using (var stillThere = await reader.GetAsync($"/api/v1/devices/{device.Id:D}", token)) Assert.AreEqual(HttpStatusCode.OK, stillThere.StatusCode);
        await using (var db = await serviceFactory.CreateDbContextAsync(token))
            Assert.IsTrue(await db.Services.AnyAsync(s => s.Id == serviceId, token));
        var ungroupedServices = await reader.GetFromJsonAsync<GroupServicePage>("/api/v1/groups/ungrouped-services", token);
        Assert.IsNotNull(ungroupedServices);
        Assert.AreEqual(1, ungroupedServices.Total);
        var other = await reader.GetFromJsonAsync<GroupDetails>($"/api/v1/groups/{group2.Id:D}", token);
        Assert.IsNotNull(other);
        Assert.AreEqual(1, other.DeviceCount);
        Assert.AreEqual(0, other.ServiceCount);
        var remaining = await reader.GetFromJsonAsync<GroupDevicePage>("/api/v1/groups/ungrouped-devices", token);
        Assert.IsNotNull(remaining);
        Assert.AreEqual(0, remaining.Total);
        using var readerClient = Client(factory);
        await LoginAsync(readerClient, "reader", "Reader-Password-123", token);
        using var readerPage = await readerClient.GetAsync($"/groups/{group2.Id:D}", token);
        var html = await readerPage.Content.ReadAsStringAsync(token);
        Assert.AreEqual(HttpStatusCode.OK, readerPage.StatusCode);
        Assert.DoesNotContain("確認刪除群組", html);
        Assert.DoesNotContain("加入所選設備", html);
        Assert.DoesNotContain("加入所選服務", html);
        using var forbidden = await readerClient.PostAsync($"/groups/{group2.Id:D}", new FormUrlEncodedContent(fields), token);
        Assert.AreNotEqual(HttpStatusCode.Redirect, forbidden.StatusCode);
        using var auditScope = factory.Services.CreateScope();
        var audit = auditScope.ServiceProvider.GetRequiredService<OperationAuditQueryService>();
        var events = await audit.QueryGroupsAsync(new OperationAuditQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)), token);
        Assert.HasCount(8, events.Items);
        Assert.AreEqual(3, events.Items.Count(x => x.Group?.Action == GroupAction.ServicesChanged));
        Assert.IsTrue(events.Items.Any(x => x.ResourceId == group.Id && x.Group?.Action == GroupAction.Deleted));
        using (var auditPage = await adminClient.GetAsync("/settings/audit?source=groups", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, auditPage.StatusCode);
            Assert.Contains("變更服務", WebUtility.HtmlDecode(await auditPage.Content.ReadAsStringAsync(token)));
        }
        using (var readerAudit = await readerClient.GetAsync("/settings/audit?source=groups", token))
            Assert.AreNotEqual(HttpStatusCode.OK, readerAudit.StatusCode);
        using var parallelGroupOneResponse = await writer.PostAsJsonAsync("/api/v1/groups/", new GroupInput("Parallel A", null), token);
        using var parallelGroupTwoResponse = await writer.PostAsJsonAsync("/api/v1/groups/", new GroupInput("Parallel B", null), token);
        var parallelGroupOne = await parallelGroupOneResponse.Content.ReadFromJsonAsync<GroupDetails>(cancellationToken: token);
        var parallelGroupTwo = await parallelGroupTwoResponse.Content.ReadFromJsonAsync<GroupDetails>(cancellationToken: token);
        Assert.IsNotNull(parallelGroupOne);
        Assert.IsNotNull(parallelGroupTwo);
        var concurrent = await Task.WhenAll(
            writer.PutAsJsonAsync($"/api/v1/groups/{parallelGroupOne.Id:D}/devices", new GroupMembershipChange(parallelGroupOne.Version, [device.Id], []), token),
            writer.PutAsJsonAsync($"/api/v1/groups/{parallelGroupTwo.Id:D}/devices", new GroupMembershipChange(parallelGroupTwo.Version, [device.Id], []), token));
        try { foreach (var response in concurrent) Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); }
        finally { foreach (var response in concurrent) response.Dispose(); }
        await using (var operations = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            await operations.Database.ExecuteSqlRawAsync("CREATE TRIGGER operations.FailGroupAudit ON operations.AuditEvents AFTER INSERT AS THROW 51000, 'audit unavailable', 1", token);
        var groupManager = auditScope.ServiceProvider.GetRequiredService<GroupManagement>();
        var createFailure = await Assert.ThrowsExactlyAsync<SqlException>(() => groupManager.CreateAsync(new GroupInput("Must roll back", null), events.Items[0].ActorId, token));
        var deleteFailure = await Assert.ThrowsExactlyAsync<SqlException>(() => groupManager.DeleteAsync(group2.Id, other.Version, events.Items[0].ActorId, token));
        Assert.AreEqual(51000, createFailure.Number);
        Assert.AreEqual(51000, deleteFailure.Number);
        try
        {
            using var failedChange = await writer.PutAsJsonAsync($"/api/v1/groups/{group2.Id:D}/services", new GroupMembershipChange(other.Version, [serviceId], []), token);
            Assert.AreEqual(HttpStatusCode.InternalServerError, failedChange.StatusCode);
        }
        catch (SqlException failure) { Assert.AreEqual(51000, failure.Number); }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            Assert.IsFalse(await db.Groups.AnyAsync(x => x.Name == "Must roll back", token));
            Assert.IsTrue(await db.Groups.AnyAsync(x => x.Id == group2.Id, token));
            Assert.IsTrue(await db.DeviceGroups.AnyAsync(x => x.GroupId == group2.Id && x.DeviceId == device.Id, token));
            Assert.IsFalse(await db.ServiceGroups.AnyAsync(x => x.GroupId == group2.Id && x.ServiceId == serviceId, token));
            var persistedVersion = await db.Groups.Where(x => x.Id == group2.Id).Select(x => x.Version).SingleAsync(token);
            Assert.AreEqual(other.Version, Convert.ToBase64String(persistedVersion));
        }
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
