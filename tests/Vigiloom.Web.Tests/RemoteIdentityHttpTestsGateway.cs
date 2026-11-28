extern alias GatewayHost;
extern alias IdentityHost;
extern alias ResourcesHost;
extern alias HostMonitoring;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.Resources.Application.AgentIdentity;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RemoteIdentityHttpTestsGateway(TestContext context)
{
    [TestMethod]
    public Task GatewayUsesRealIdentityAndResourcesForAuthorization() =>
        ExecuteGatewayScenarioAsync(false);

    [TestMethod]
    [TestCategory("Browser")]
    public Task GatewayAllowsWebAssemblyAgentVersionAssignment() =>
        ExecuteGatewayScenarioAsync(true);

    private Task ExecuteGatewayScenarioAsync(bool verifyBrowser) =>
        ReusableWebDatabase.ExecuteAsync(async (webDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (identityDb, innerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourceDb, token) =>
        {
            var keys = new EphemeralDataProtectionProvider();
            using var identity = new WebApplicationFactory<IdentityHost::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:VigiloomIdentity"] = identityDb.ConnectionString }));
            });
            identity.UseKestrel(0);
            identity.StartServer();
            var identityAddress = Address(identity.Services);
            var identityFactory = identity.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await identityFactory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(identityFactory, token);
            await using var identityScope = identity.Services.CreateAsyncScope();
            var users = identityScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var reader = new Account { UserName = "gateway-reader" };
            var admin = new Account { UserName = "gateway-admin" };
            Assert.IsTrue((await users.CreateAsync(reader, "Reader-Password-123")).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(reader, BuiltInRoles.ReadOnly)).Succeeded);
            Assert.IsTrue((await users.CreateAsync(admin, "Admin-Password-123")).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(admin, BuiltInRoles.Administrator)).Succeeded);
            var secret = RandomNumberGenerator.GetBytes(32);
            var credential = new ApiCredential("gateway", SHA256.HashData(secret), true, DateTimeOffset.UtcNow.AddDays(1), admin.Id);
            await using (var db = await identityFactory.CreateDbContextAsync(token)) { db.ApiCredentials.Add(credential); await db.SaveChangesAsync(token); }

            using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Resources"] = resourceDb.ConnectionString, ["Services:Identity:Url"] = identityAddress,
                  ["Vigiloom:Gateway:TrustedProxies:0"] = "127.0.0.1" }));
                builder.ConfigureTestServices(services =>
                {
                    var dispatcher = services.SingleOrDefault(item => item.ImplementationType?.Name == "WorkDispatcher");
                    if (dispatcher is not null) services.Remove(dispatcher);
                    services.RemoveAll<IDataProtectionProvider>();
                    services.AddSingleton<IDataProtectionProvider>(keys);
                    services.RemoveAll<IPackageLibraryTransport>();
                    services.AddSingleton<IPackageLibraryTransport, Vigiloom.Tests.Fixtures.AvailablePackageTransport>();
                });
            });
            resources.UseKestrel(0);
            resources.StartServer();
            var resourcesAddress = Address(resources.Services);
            await using (var db = await resources.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var scheduleOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(resourceDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(scheduleOptions)) await db.Database.MigrateAsync(token);

            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(resourceDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var db = new HostMonitoringDbContext(hostOptions)) await db.Database.MigrateAsync(token);
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseUrls("http://127.0.0.1:0");
                builder.UseSetting("ConnectionStrings:HostMonitoring", resourceDb.ConnectionString);
                builder.UseSetting("Services:Resources:Url", resourcesAddress);
                builder.UseSetting("Hangfire:Enabled", "false");
                builder.UseSetting("Temporal:Enabled", "false");
            });
            host.UseKestrel(0);
            host.StartServer();
            var hostAddress = Address(host.Services);
            resources.Services.GetRequiredService<IConfiguration>()["Services:Host:Url"] = hostAddress;

            using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Vigiloom"] = webDb.ConnectionString, ["Services:Identity:Url"] = identityAddress, ["Services:Resources:Url"] = resourcesAddress,
                  ["Services:HostMonitoring:Url"] = hostAddress }));
                builder.ConfigureTestServices(services => { services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(keys); });
            });
            web.UseKestrel(0);
            web.StartServer();
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(web.Services), ["Services:Identity:Url"] = identityAddress,
                  ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = resourcesAddress }));
                builder.ConfigureTestServices(services => { services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(keys); });
            });
            var options = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false };
            using var anonymous = gateway.CreateClient(options);
            using (var denied = await anonymous.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await anonymous.GetAsync("/api/v1/groups/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var readClient = gateway.CreateClient(options);
            var readerCookie = await LoginAsync(readClient, "gateway-reader", "Reader-Password-123", token);
            using (var read = await readClient.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            using (var groups = await readClient.GetAsync("/api/v1/groups/", token)) Assert.AreEqual(HttpStatusCode.OK, groups.StatusCode);
            using (var denied = await readClient.PostAsJsonAsync("/api/v1/groups/", new GroupInput("denied", null), token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var packages = await readClient.GetAsync("/api/v1/agent-packages", token)) Assert.AreEqual(HttpStatusCode.OK, packages.StatusCode);
            using (var deniedManagement = await readClient.GetAsync($"/api/v1/agent-management/devices/{Guid.CreateVersion7():D}", token)) Assert.AreEqual(HttpStatusCode.Forbidden, deniedManagement.StatusCode);
            using var secondWeb = web.WithWebHostBuilder(_ => { });
            secondWeb.UseKestrel(0);
            secondWeb.StartServer();
            using var secondGateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(secondWeb.Services), ["Services:Identity:Url"] = identityAddress,
                  ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = resourcesAddress }));
                builder.ConfigureTestServices(services => { services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(keys); });
            });
            using var secondInstance = secondGateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = options.BaseAddress, AllowAutoRedirect = false, HandleCookies = false });
            secondInstance.DefaultRequestHeaders.Add("Cookie", readerCookie);
            using (var read = await secondInstance.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            using (var forbidden = await readClient.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("denied", "example.org", [], false), token)) Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var adminClient = gateway.CreateClient(options);
            await LoginAsync(adminClient, "gateway-admin", "Admin-Password-123", token);
            using (var missingDevice = await adminClient.GetAsync($"/api/v1/agent-management/devices/{Guid.CreateVersion7():D}", token)) Assert.AreEqual(HttpStatusCode.NotFound, missingDevice.StatusCode);
            using (var anti = await adminClient.GetAsync("/api/v1/session/antiforgery", token))
            {
                using var json = JsonDocument.Parse(await anti.Content.ReadAsStringAsync(token));
                adminClient.DefaultRequestHeaders.Add("RequestVerificationToken", json.RootElement.GetProperty("requestToken").GetString());
            }
            using (var missingAgent = await adminClient.PostAsync($"/api/v1/agent-management/agents/{Guid.CreateVersion7():D}/revoke", null, token))
                Assert.AreEqual(HttpStatusCode.NotFound, missingAgent.StatusCode);
            using (var created = await adminClient.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("gateway device", "example.org", [], false), token)) Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            using (var created = await adminClient.PostAsJsonAsync("/api/v1/groups/", new GroupInput("gateway group", null), token)) Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            using var createdHost = await adminClient.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("gateway host", "host.example", [], true), token);
            Assert.AreEqual(HttpStatusCode.Created, createdHost.StatusCode);
            var hostDevice = await createdHost.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token);
            Assert.IsNotNull(hostDevice);
            await using var resourcesScope = resources.Services.CreateAsyncScope();
            var registrations = resourcesScope.ServiceProvider.GetRequiredService<AgentRegistration>();
            var issued = await registrations.IssueAsync(hostDevice.Id, admin.Id, token);
            Assert.AreEqual(AgentRegistrationStatus.Success, issued.Status);
            var machineSecret = RandomNumberGenerator.GetBytes(32);
            using var enrolled = await anonymous.PostAsJsonAsync("/api/v1/agent/enroll", new { EnrollmentSecret = issued.Secret, MachineVerifier = SHA256.HashData(machineSecret) }, token);
            Assert.AreEqual(HttpStatusCode.OK, enrolled.StatusCode);
            using var enrollmentJson = JsonDocument.Parse(await enrolled.Content.ReadAsStringAsync(token));
            var agentId = enrollmentJson.RootElement.GetProperty("agentId").GetGuid();
            if (verifyBrowser)
            {
                var packageId = Guid.CreateVersion7();
                await using (var db = await resources.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                {
                    db.AgentPackages.Add(new Vigiloom.Modules.Resources.Domain.AgentPackage
                    {
                        Id = packageId, Version = "1.2.3", RuntimeIdentifier = "win-x64", Size = 1,
                        Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64)
                    });
                    await db.SaveChangesAsync(token);
                }
                using (var browserGateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseUrls("http://127.0.0.1:0");
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ReverseProxy:Clusters:web:Destinations:web:Address"] = Address(web.Services),
                        ["ReverseProxy:Clusters:resources:Destinations:resources:Address"] = resourcesAddress,
                        ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = hostAddress,
                        ["Services:Identity:Url"] = identityAddress
                    }));
                    builder.ConfigureTestServices(services => { services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(keys); });
                }))
                {
                    browserGateway.UseKestrel(0);
                    browserGateway.StartServer();
                    using var playwright = await Playwright.CreateAsync();
                    await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
                    var page = await browser.NewPageAsync();
                    await using var renderGate = await AutoRenderGate.HoldAsync(page);
                    await page.GotoAsync(Address(browserGateway.Services) + $"/devices/{hostDevice.Id:D}/agent-version");
                    await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("gateway-admin");
                    await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Admin-Password-123");
                    await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
                    await page.Locator(".version-assignment[data-interactive='true']").WaitForAsync();
                    await renderGate.ReleaseAsync();
                    await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
                    await page.ReloadAsync();
                    await page.Locator(".version-assignment[data-runtime='WebAssembly'][data-interactive='true']").WaitForAsync();
                    await page.GetByRole(AriaRole.Radio, new() { Name = "1.2.3 win-x64" }).CheckAsync();
                    await page.GetByRole(AriaRole.Button, new() { Name = "指派版本", Exact = true }).ClickAsync();
                    await page.GetByText("已儲存版本指派；設備是否安裝仍以實際回報為準。", new() { Exact = true }).WaitForAsync();
                    await using (var db = await resources.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                    {
                        var assignment = await db.AgentVersionAssignments.SingleAsync(x => x.AgentId == agentId, token);
                        Assert.AreEqual(packageId, assignment.PackageId);
                        Assert.IsNotNull(assignment.UpdateId);
                    }
                    await page.GetByRole(AriaRole.Button, new() { Name = "取消指派", Exact = true }).ClickAsync();
                    await page.GetByText("已取消指派。", new() { Exact = true }).WaitForAsync();
                    await using (var db = await resources.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                    {
                        var assignment = await db.AgentVersionAssignments.SingleAsync(x => x.AgentId == agentId, token);
                        Assert.IsNull(assignment.PackageId);
                        Assert.IsNull(assignment.UpdateId);
                    }
                }
            }
            Assert.IsTrue(AgentCredentials.TryDecode(issued.Secret, "vle", out var enrollmentId, out var enrollmentSecret));
            CryptographicOperations.ZeroMemory(enrollmentSecret);
            anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AgentCredentials.Encode("vlr", enrollmentId, machineSecret));
            using (var recovered = await anonymous.PostAsJsonAsync("/api/v1/agent/recover", new { EnrollmentSecret = issued.Secret }, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
                using var recoveredJson = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync(token));
                Assert.AreEqual(agentId, recoveredJson.RootElement.GetProperty("agentId").GetGuid());
            }
            var oldCredential = AgentCredentials.Encode("vla", agentId, machineSecret);
            var replacementSecret = RandomNumberGenerator.GetBytes(32);
            anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldCredential);
            using (var rotated = await anonymous.PostAsJsonAsync("/api/v1/agent/rotation/start", new { MachineVerifier = SHA256.HashData(replacementSecret) }, token)) Assert.AreEqual(HttpStatusCode.NoContent, rotated.StatusCode);
            var replacementCredential = AgentCredentials.Encode("vla", agentId, replacementSecret);
            anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replacementCredential);
            using (var confirmed = await anonymous.PostAsync("/api/v1/agent/rotation/finish", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, confirmed.StatusCode);
            Assert.IsNull(await registrations.AuthenticateAsync(oldCredential, token));
            Assert.IsNotNull(await registrations.AuthenticateAsync(replacementCredential, token));
            await using (var db = await resources.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                Assert.IsTrue(await db.Devices.AnyAsync(device => device.Name == "gateway device", token));
            var webResourcesOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(webDb.ConnectionString).Options;
            await using (var db = new ResourcesDbContext(webResourcesOptions))
                Assert.IsFalse(await db.Devices.AnyAsync(device => device.Name == "gateway device", token));
            using var api = gateway.CreateClient(options);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(secret)}");
            using (var valid = await api.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
            using (var groups = await api.GetAsync("/api/v1/groups/", token)) Assert.AreEqual(HttpStatusCode.OK, groups.StatusCode);
            using (var packages = await api.GetAsync("/api/v1/agent-packages", token)) Assert.AreEqual(HttpStatusCode.OK, packages.StatusCode);
            using (var deniedManagement = await api.GetAsync($"/api/v1/agent-management/devices/{Guid.CreateVersion7():D}", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, deniedManagement.StatusCode);
            await using (var db = await identityFactory.CreateDbContextAsync(token))
            { var stored = await db.ApiCredentials.SingleAsync(x => x.Id == credential.Id, token); stored.Revoke(DateTimeOffset.UtcNow); await db.SaveChangesAsync(token); }
            using (var revoked = await api.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.IsTrue((await users.UpdateSecurityStampAsync(reader)).Succeeded);
            using (var revoked = await readClient.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using (var revoked = await secondInstance.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }, innerToken), outerToken), context.CancellationToken);

    private static string Address(IServiceProvider services) => services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static async Task<string> LoginAsync(HttpClient client, string username, string password, CancellationToken token)
    {
        using var page = await client.GetAsync("/account/login", token);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
        fields["Input.UserName"] = username; fields["Input.Password"] = password;
        using var login = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
        return string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));
    }
}
