extern alias HostMonitoring;
extern alias ResourcesHost;
extern alias GatewayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Identity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Tests.Fixtures;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentVersionServicesTests(TestContext context)
{
    [TestMethod]
    public Task HostAcceptsUpdateResultForRealResourcesAssignment() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (resourceDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDb, token) =>
        {
            var resourcesOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(resourceDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(resourceDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new ResourcesDbContext(resourcesOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var db = new HostMonitoringDbContext(hostOptions)) await db.Database.MigrateAsync(token);

            var now = DateTimeOffset.UtcNow;
            var device = new Device("server", "server.example", true, now);
            var secret = RandomNumberGenerator.GetBytes(32);
            var agentId = Guid.CreateVersion7();
            byte[] archive = [1, 2, 3, 4];
            var archiveHash = Convert.ToHexStringLower(SHA256.HashData(archive));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version = "1.2.3", rid = "win-x64", size = archive.Length, sha256 = archiveHash });
            var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
            var package = new Vigiloom.Modules.Resources.Domain.AgentPackage { Id = Guid.CreateVersion7(), Version = "1.2.3", RuntimeIdentifier = "win-x64", Sha256 = archiveHash, ManifestSha256 = manifestHash, Size = archive.Length };
            var otherDevice = new Device("other server", "other.example", true, now);
            var otherAgentId = Guid.CreateVersion7();
            var otherSecret = RandomNumberGenerator.GetBytes(32);
            await using (var db = new ResourcesDbContext(resourcesOptions))
            {
                db.Devices.AddRange(device, otherDevice);
                db.Agents.Add(new RegisteredAgent { Id = agentId, DeviceId = device.Id, DeviceGeneration = device.AgentAuthorizationGeneration, Verifier = SHA256.HashData(secret), CreatedAt = now });
                db.Agents.Add(new RegisteredAgent { Id = otherAgentId, DeviceId = otherDevice.Id, DeviceGeneration = otherDevice.AgentAuthorizationGeneration, Verifier = SHA256.HashData(otherSecret), CreatedAt = now });
                db.AgentPackages.Add(package);
                await db.SaveChangesAsync(token);
            }

            var cachedPackage = new CachedPackage(manifest, archive);
            var session = new AdminSessionHandler();
            using var releasesHttp = new HttpClient(new ReleaseHandler(archive.Length, archiveHash)) { BaseAddress = new Uri("https://api.github.com/") };
            using var resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Resources"] = resourceDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.RemoveAll<IPackageLibraryTransport>();
                    services.AddSingleton<IPackageLibraryTransport>(cachedPackage);
                    services.RemoveAll<GitHubAgentReleases>();
                    services.AddSingleton(new GitHubAgentReleases(releasesHttp));
                    services.RemoveAll<IOutboxHandler>();
                    services.AddScoped<IOutboxHandler, TestOutboxHandler>();
                    services.AddHttpClient<IdentityCookieValidator>().ConfigurePrimaryHttpMessageHandler(() => session);
                });
            });
            resources.UseKestrel(0);
            using var resourceClient = resources.CreateClient();
            using (var api = await resourceClient.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", token))
            {
                var paths = api!.RootElement.GetProperty("paths");
                var packages = paths.GetProperty("/api/v1/agent-packages").GetProperty("get").GetProperty("security");
                Assert.IsTrue(packages.EnumerateArray().Any(x => x.TryGetProperty("BrowserSession", out _)));
                Assert.IsTrue(packages.EnumerateArray().Any(x => x.TryGetProperty("ApiBearer", out _)));
                var cache = paths.GetProperty("/api/v1/agent-packages/cache").GetProperty("post").GetProperty("security");
                Assert.AreEqual(1, cache.GetArrayLength());
                Assert.IsTrue(cache[0].TryGetProperty("BrowserSession", out _));
                Assert.IsTrue(cache[0].TryGetProperty("Antiforgery", out _));
                var path = paths.GetProperty("/api/v1/agents/{id}/version-assignment");
                var read = path.GetProperty("get").GetProperty("security");
                Assert.IsTrue(read.EnumerateArray().Any(x => x.TryGetProperty("BrowserSession", out _)));
                Assert.IsTrue(read.EnumerateArray().Any(x => x.TryGetProperty("ApiBearer", out _)));
                var write = path.GetProperty("put");
                Assert.IsTrue(write.GetProperty("security")[0].TryGetProperty("BrowserSession", out _));
                Assert.IsTrue(write.GetProperty("security")[0].TryGetProperty("Antiforgery", out _));
                Assert.IsTrue(write.GetProperty("responses").TryGetProperty("409", out _));
            }
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString,
                    ["Services:Resources:Url"] = resourceClient.BaseAddress!.ToString()
                }));
                web.ConfigureTestServices(services =>
                    services.AddHttpClient<IdentityCookieValidator>().ConfigurePrimaryHttpMessageHandler(() => session));
            });
            host.UseKestrel(0);
            using var hostClient = host.CreateClient();
            using (var api = await hostClient.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", token))
            {
                var paths = api!.RootElement.GetProperty("paths");
                foreach (var path in new[] { "/api/v1/agent/update/targets/{name}", "/api/v1/agent/update/packages/{name}" })
                {
                    var operation = paths.GetProperty(path).GetProperty("get");
                    Assert.IsTrue(operation.GetProperty("security")[0].TryGetProperty("AgentBearer", out _));
                    Assert.IsTrue(operation.GetProperty("responses").TryGetProperty("503", out _));
                }
                var assignment = paths.GetProperty("/api/v1/agent/version-assignment").GetProperty("get");
                Assert.IsTrue(assignment.GetProperty("security")[0].TryGetProperty("AgentBearer", out _));
                Assert.IsTrue(assignment.GetProperty("responses").TryGetProperty("204", out _));
            }
            resources.Services.GetRequiredService<IConfiguration>()["Services:Host:Url"] = hostClient.BaseAddress!.ToString();
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vigiloom:Gateway:AgentIdentityAddress"] = hostClient.BaseAddress!.ToString(),
                    ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = hostClient.BaseAddress!.ToString()
                })));
            using var gatewayClient = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

            var assignmentPath = $"/api/v1/agents/{agentId:D}/version-assignment";
            using (var denied = await resourceClient.GetAsync(assignmentPath, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await resourceClient.GetAsync("/api/v1/agent-packages", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            hostClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AgentCredentials.Encode("vla", agentId, secret));
            using (var unassigned = await hostClient.GetAsync("/api/v1/agent/version-assignment", token))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, unassigned.StatusCode);
                Assert.IsTrue(unassigned.Headers.CacheControl?.NoStore);
            }
            var cookieOptions = resources.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var adminIdentity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, session.AccountId.ToString("D")),
                new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
            var principal = new ClaimsPrincipal(adminIdentity);
            var cookie = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme));
            resourceClient.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}");
            using (var missingCsrf = await resourceClient.PutAsJsonAsync(assignmentPath, new AgentVersionAssignmentUpdate(package.Id, ""), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            using (var missingCsrf = await resourceClient.PostAsJsonAsync("/api/v1/agent-packages/cache", new { Version = "1.2.3", RuntimeIdentifier = "win-x64" }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            using (var missingCsrf = await resourceClient.DeleteAsync($"/api/v1/agent-packages/{package.Id:D}/cache", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            var antiContext = new DefaultHttpContext { RequestServices = resources.Services, User = principal };
            antiContext.Request.Scheme = "https";
            var anti = resources.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(antiContext);
            var antiCookie = antiContext.Response.Headers.SetCookie.Single()!.Split(';')[0];
            resourceClient.DefaultRequestHeaders.Remove("Cookie");
            resourceClient.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}; {antiCookie}");
            resourceClient.DefaultRequestHeaders.Add("RequestVerificationToken", anti.RequestToken);
            using (var cached = await resourceClient.PostAsJsonAsync("/api/v1/agent-packages/cache", new { Version = "1.2.3", RuntimeIdentifier = "win-x64" }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, cached.StatusCode);
            using (var missing = await resourceClient.PostAsJsonAsync("/api/v1/agent-packages/cache", new { Version = "1.2.3", RuntimeIdentifier = "unknown" }, token))
                Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            using (var assigned = await resourceClient.PutAsJsonAsync(assignmentPath, new AgentVersionAssignmentUpdate(package.Id, ""), token))
                Assert.AreEqual(HttpStatusCode.NoContent, assigned.StatusCode);
            using (var inUse = await resourceClient.DeleteAsync($"/api/v1/agent-packages/{package.Id:D}/cache", token))
                Assert.AreEqual(HttpStatusCode.Conflict, inUse.StatusCode);
            using (var stale = await resourceClient.PutAsJsonAsync(assignmentPath, new AgentVersionAssignmentUpdate(package.Id, ""), token))
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            var publicAssignment = await resourceClient.GetFromJsonAsync<AgentVersionAssignmentView>(assignmentPath, token);
            Assert.AreEqual(package.Id, publicAssignment!.PackageId);
            session.Role = "Read only";
            using (var denied = await resourceClient.PutAsJsonAsync(assignmentPath, new AgentVersionAssignmentUpdate(null, publicAssignment.Revision), token))
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await resourceClient.PostAsJsonAsync("/api/v1/agent-packages/cache", new { Version = "1.2.3", RuntimeIdentifier = "win-x64" }, token))
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await resourceClient.DeleteAsync($"/api/v1/agent-packages/{package.Id:D}/cache", token))
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            session.Role = "Administrator";
            using (var cancelled = await resourceClient.PutAsJsonAsync(assignmentPath, new AgentVersionAssignmentUpdate(null, publicAssignment.Revision), token))
                Assert.AreEqual(HttpStatusCode.NoContent, cancelled.StatusCode);
            using (var noTarget = await hostClient.GetAsync("/api/v1/agent/version-assignment", token))
                Assert.AreEqual(HttpStatusCode.NoContent, noTarget.StatusCode);
            var clearedAssignment = await resourceClient.GetFromJsonAsync<AgentVersionAssignmentView>(assignmentPath, token);
            using (var reassigned = await resourceClient.PutAsJsonAsync(assignmentPath, new AgentVersionAssignmentUpdate(package.Id, clearedAssignment!.Revision), token))
                Assert.AreEqual(HttpStatusCode.NoContent, reassigned.StatusCode);
            using (var cookieOnly = host.CreateClient())
            {
                cookieOnly.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}");
                using var denied = await cookieOnly.GetAsync("/api/v1/agent/version-assignment", token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            var target = await resourceClient.GetFromJsonAsync<AgentVersionTarget>($"/internal/v1/agents/{agentId:D}/target", token);
            Assert.IsNotNull(target);
            gatewayClient.DefaultRequestHeaders.Authorization = hostClient.DefaultRequestHeaders.Authorization;
            var identity = await hostClient.GetFromJsonAsync<JsonElement>("/api/v1/agent/identity", token);
            Assert.AreEqual(agentId, identity.GetProperty("agentId").GetGuid());
            Assert.AreEqual(device.Id, identity.GetProperty("deviceId").GetGuid());
            var gatewayIdentity = await gatewayClient.GetFromJsonAsync<JsonElement>("/api/v1/agent/identity", token);
            Assert.AreEqual(agentId, gatewayIdentity.GetProperty("agentId").GetGuid());
            Assert.AreEqual(device.Id, gatewayIdentity.GetProperty("deviceId").GetGuid());
            var hostTarget = await hostClient.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(target, hostTarget);
            CollectionAssert.AreEqual(manifest, await hostClient.GetByteArrayAsync($"/api/v1/agent/update/targets/{manifestHash}.json", token));
            CollectionAssert.AreEqual(archive, await hostClient.GetByteArrayAsync($"/api/v1/agent/update/packages/{archiveHash}", token));
            var gatewayTarget = await gatewayClient.GetFromJsonAsync<AgentVersionTarget>("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(target, gatewayTarget);
            CollectionAssert.AreEqual(manifest, await gatewayClient.GetByteArrayAsync($"/api/v1/agent/update/targets/{manifestHash}.json", token));
            CollectionAssert.AreEqual(archive, await gatewayClient.GetByteArrayAsync($"/api/v1/agent/update/packages/{archiveHash}", token));
            cachedPackage.FailRead = true;
            using (var unavailable = await hostClient.GetAsync($"/api/v1/agent/update/targets/{manifestHash}.json", token))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
                Assert.AreEqual("application/problem+json", unavailable.Content.Headers.ContentType!.MediaType);
            }
            cachedPackage.FailRead = false;
            using (var wrongPackage = await hostClient.GetAsync("/api/v1/agent/update/packages/missing", token))
                Assert.AreEqual(HttpStatusCode.NotFound, wrongPackage.StatusCode);
            using (var other = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/agent/update/packages/{archiveHash}"))
            {
                other.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AgentCredentials.Encode("vla", otherAgentId, otherSecret));
                using var denied = await hostClient.SendAsync(other, token);
                Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
            }
            var result = new AgentUpdateResult(Guid.CreateVersion7(), target.UpdateId, target.PackageId, target.ManifestSha256, 3, AgentUpdateResultKind.Succeeded, now, false, null);
            using var received = await hostClient.PostAsJsonAsync("/api/v1/agent/update-results", result, token);
            Assert.AreEqual(HttpStatusCode.NoContent, received.StatusCode);
            using (var retried = await hostClient.PostAsJsonAsync("/api/v1/agent/update-results", result, token))
                Assert.AreEqual(HttpStatusCode.NoContent, retried.StatusCode);
            var delayed = result with { MessageId = Guid.CreateVersion7(), Sequence = 1, Result = AgentUpdateResultKind.Switching };
            using (var delayedResponse = await hostClient.PostAsJsonAsync("/api/v1/agent/update-results", delayed, token))
                Assert.AreEqual(HttpStatusCode.NoContent, delayedResponse.StatusCode);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using (var duplicate = await hostClient.PostAsync("/api/v1/agent/update-results",
                new StringContent(json.Insert(1, $"\"messageId\":\"{result.MessageId:D}\","), Encoding.UTF8, "application/json"), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using (var oversized = await hostClient.PostAsync("/api/v1/agent/update-results",
                new StringContent(new string('x', 4097), Encoding.UTF8, "application/json"), token))
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            var status = await resourceClient.GetFromJsonAsync<AgentVersionAssignmentView>($"/internal/v1/agents/{agentId:D}/assignment", token);
            Assert.AreEqual(AgentUpdateResultKind.Succeeded, status!.LatestResult!.Result);
            Assert.AreEqual(package.Version, status.LatestResult.PackageVersion);
            var journalPath = Path.Combine(AppContext.BaseDirectory, "agent-result-" + Guid.CreateVersion7().ToString("N"));
            try
            {
                var journal = new UpdateJournal(journalPath);
                var provenance = new UpdateProvenance(agentId, package.Id, manifestHash);
                await journal.WriteAsync(new UpdateState(target.UpdateId, Path.Combine(journalPath, "old"), Path.Combine(journalPath, "new"),
                    UpdatePhase.Succeeded, now, [new(UpdatePhase.Prepared, now, null), new(UpdatePhase.Succeeded, now, null)], Provenance: provenance), token);
                using var deliveryClient = new HttpClient(new DropFirstResponseHandler(hostClient.BaseAddress!));
                var delivery = new UpdateResultDelivery(deliveryClient, journal, TimeProvider.System);
                var credential = AgentCredentials.Encode("vla", agentId, secret);
                Assert.AreEqual(0, await delivery.FlushAsync(new Uri("https://localhost/"), credential, token));
                Assert.HasCount(1, await journal.ReadPendingAsync(agentId, 10, token));
                Assert.AreEqual(1, await delivery.FlushAsync(new Uri("https://localhost/"), credential, token));
                Assert.IsEmpty(await journal.ReadPendingAsync(agentId, 10, token));
                await using var verify = new HostMonitoringDbContext(hostOptions);
                Assert.AreEqual(1, await verify.UpdateReceipts.CountAsync(x => x.AgentId == agentId && x.UpdateId == target.UpdateId && x.Sequence == 2, token));
            }
            finally
            {
                var root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(journalPath).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated root.");
                if (Directory.Exists(journalPath)) Directory.Delete(journalPath, recursive: true);
            }
            cachedPackage.BeforeOpenReturns = async () =>
            {
                using var cleared = await resourceClient.PutAsJsonAsync($"/internal/v1/agents/{agentId:D}/assignment",
                    new AgentVersionAssignmentCommand(null, status.Revision, Guid.CreateVersion7()), token);
                Assert.AreEqual(VersionAssignmentResult.Saved, await cleared.Content.ReadFromJsonAsync<VersionAssignmentResult>(cancellationToken: token));
            };
            using (var clearedDuringDownload = await hostClient.GetAsync($"/api/v1/agent/update/packages/{archiveHash}", token))
                Assert.AreEqual(HttpStatusCode.NotFound, clearedDuringDownload.StatusCode);
            Assert.IsFalse(cachedPackage.LastStream!.CanRead);
            using (var removed = await resourceClient.DeleteAsync($"/api/v1/agent-packages/{package.Id:D}/cache", token))
                Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
            using var revoked = await resourceClient.PostAsJsonAsync($"/internal/v1/agents/{agentId:D}/revoke", new AgentRevokeCommand(Guid.CreateVersion7()), token);
            Assert.AreEqual(AgentRegistrationStatus.Success, await revoked.Content.ReadFromJsonAsync<AgentRegistrationStatus>(cancellationToken: token));
            using var rejected = await hostClient.PostAsJsonAsync("/api/v1/agent/update-results", result, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
            using var revokedAssignment = await hostClient.GetAsync("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revokedAssignment.StatusCode);
            using var revokedPackage = await hostClient.GetAsync($"/api/v1/agent/update/packages/{archiveHash}", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revokedPackage.StatusCode);
            using var gatewayRevoked = await gatewayClient.GetAsync("/api/v1/agent/version-assignment", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, gatewayRevoked.StatusCode);
        }, outerToken), context.CancellationToken);

    private sealed class TestOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, Microsoft.Data.SqlClient.SqlConnection connection, Microsoft.Data.SqlClient.SqlTransaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AdminSessionHandler : HttpMessageHandler
    {
        public Guid AccountId { get; } = Guid.CreateVersion7();
        public string Role { get; set; } = "Administrator";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValidatedCookieResponse([
                new(ClaimTypes.NameIdentifier, AccountId.ToString("D"), ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY"),
                new(ClaimTypes.Role, Role, ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY")])) });
    }

    private sealed class DropFirstResponseHandler(Uri host) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        private bool drop = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            request.RequestUri = new Uri(host, request.RequestUri!.PathAndQuery);
            var response = await base.SendAsync(request, token);
            if (!drop) return response;
            drop = false;
            response.Dispose();
            throw new HttpRequestException("Simulated lost response after commit.");
        }
    }

    private sealed class CachedPackage(byte[] manifest, byte[] archive) : IPackageLibraryTransport
    {
        public Func<Task>? BeforeOpenReturns { get; set; }
        public MemoryStream? LastStream { get; private set; }
        public bool FailRead { get; set; }
        public bool IsCached(string archiveHash, string manifestHash) => true;
        public Task<byte[]?> ReadManifestAsync(string hash, CancellationToken token) =>
            FailRead ? throw new IOException("Package unavailable") : Task.FromResult<byte[]?>(manifest);
        public async Task<Stream?> OpenCachedAsync(string hash, long size, CancellationToken token)
        {
            LastStream = new MemoryStream(archive, writable: false);
            if (BeforeOpenReturns is not null) await BeforeOpenReturns();
            return LastStream;
        }
        public Task SaveAsync(string version, AgentReleaseAsset asset, string manifestHash, byte[] data, GitHubAgentReleases github, CancellationToken token) => Task.CompletedTask;
        public Task RemoveCachedAsync(string archiveHash, string manifestHash, IReadOnlyList<string> others, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class ReleaseHandler(int size, string hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var json = $$"""{"tag_name":"agent-v1.2.3","assets":[{"name":"agent-win-x64.zip","size":{{size}},"digest":"sha256:{{hash}}"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
