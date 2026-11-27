using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LineEndpointTests(TestContext context)
{
    [TestMethod]
    public Task ChannelGroupAndTestDeliveryAreManagedThroughInternalApi() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var group = new LineGroup { ExternalId = "group", LastSeenAt = DateTimeOffset.UtcNow };
            var session = new SessionHandler();
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                db.Set<LineGroup>().Add(group);
                await db.SaveChangesAsync(token);
            }
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = database.ConnectionString }));
                web.ConfigureTestServices(s =>
                {
                    HostWorkers.RemoveBackgroundWork(s);
                    s.RemoveAll<IDataProtectionProvider>();
                    s.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    s.AddHttpClient<IdentityCookieValidator>().ConfigurePrimaryHttpMessageHandler(() => session);
                });
            });
            using var client = host.CreateClient();
            var actor = Guid.CreateVersion7();
            using (var saved = await client.PutAsJsonAsync("/internal/v1/line", new LineChannelChange(actor, true, "token", "secret", 15), token))
                Assert.AreEqual(HttpStatusCode.NoContent, saved.StatusCode);
            using (var saved = await client.PutAsJsonAsync($"/internal/v1/line/groups/{group.Id}", new LineGroupChange(actor, "Operations", true), token))
                Assert.AreEqual(HttpStatusCode.NoContent, saved.StatusCode);
            var settings = await client.GetFromJsonAsync<LineChannelInfo>("/internal/v1/line", token);
            Assert.IsNotNull(settings);
            Assert.AreEqual(15, settings.AggregationSeconds);
            Assert.IsTrue(settings.HasAccessToken && settings.HasChannelSecret && settings.Groups.Single().Approved);
            using var response = await client.PostAsync($"/internal/v1/line/groups/{group.Id}/test", null, token);
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
            var test = await response.Content.ReadFromJsonAsync<LineTestDelivery>(token);
            Assert.IsNotNull(test);
            var status = await client.GetFromJsonAsync<LineDeliveryInfo>($"/internal/v1/line/deliveries/{test.Id}", token);
            Assert.AreEqual("pending", status?.Status);
            var recent = await client.GetFromJsonAsync<LineDeliveryInfoSummary[]>("/internal/v1/line/deliveries", token);
            Assert.AreEqual("Operations", recent?.Single().GroupName);
            var body = Encoding.UTF8.GetBytes("""
                {"destination":"Utest","events":[{"type":"join","source":{"type":"group","groupId":"Cnew"}}]}
                """);
            async Task<HttpStatusCode> SendWebhookAsync(string signature)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook");
                request.Content = new ByteArrayContent(body);
                request.Headers.Add("x-line-signature", signature);
                using var received = await client.SendAsync(request, token);
                return received.StatusCode;
            }
            Assert.AreEqual(HttpStatusCode.Unauthorized, await SendWebhookAsync("invalid"));
            var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body));
            Assert.AreEqual(HttpStatusCode.OK, await SendWebhookAsync(signature));
            Assert.AreEqual(HttpStatusCode.OK, await SendWebhookAsync(signature));
            var malformed = Encoding.UTF8.GetBytes("{\"events\":[");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook"))
            {
                request.Content = new ByteArrayContent(malformed);
                request.Headers.Add("x-line-signature", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), malformed)));
                using var rejected = await client.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            settings = await client.GetFromJsonAsync<LineChannelInfo>("/internal/v1/line", token);
            Assert.IsFalse(settings!.Groups.Single(item => item.ExternalId == "Cnew").Approved);
            using (var anonymous = await client.GetAsync("/api/v1/admin/line", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, session.AccountId.ToString("D")),
                new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
            var cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), IdentityConstants.ApplicationScheme));
            client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={cookie}");
            var update = new { Enabled = true, AccessToken = (string?)null, ChannelSecret = (string?)null, AggregationSeconds = 20 };
            using (var missingCsrf = await client.PutAsJsonAsync("/api/v1/admin/line", update, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            var antiContext = new DefaultHttpContext { RequestServices = host.Services, User = new ClaimsPrincipal(identity) };
            antiContext.Request.Scheme = "https";
            var antiforgery = host.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(antiContext);
            var antiCookie = antiContext.Response.Headers.SetCookie.Single()!.Split(';')[0];
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={cookie}; {antiCookie}");
            client.DefaultRequestHeaders.Add("RequestVerificationToken", antiforgery.RequestToken);
            using (var saved = await client.PutAsJsonAsync("/api/v1/admin/line", update, token))
                Assert.AreEqual(HttpStatusCode.NoContent, saved.StatusCode);
            using (var invalidUpdate = await client.PutAsJsonAsync("/api/v1/admin/line", new { AccessToken = "replacement" }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
            var publicSettings = await client.GetStringAsync("/api/v1/admin/line", token);
            Assert.DoesNotContain("secret", publicSettings);
            Assert.DoesNotContain("token", publicSettings);
            var current = await client.GetFromJsonAsync<LineChannelView>("/api/v1/admin/line", token);
            Assert.AreEqual(20, current!.AggregationSeconds);
            using (var changed = await client.PutAsJsonAsync($"/api/v1/admin/line/groups/{group.Id:D}", new { Name = "Operations", Approved = true }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, changed.StatusCode);
            using (var invalidGroup = await client.PutAsJsonAsync($"/api/v1/admin/line/groups/{group.Id:D}", new { Name = "No approval" }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidGroup.StatusCode);
            using (var testDelivery = await client.PostAsync($"/api/v1/admin/line/groups/{group.Id:D}/test", null, token))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, testDelivery.StatusCode);
                var delivery = await testDelivery.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                var deliveryId = delivery.GetProperty("id").GetGuid();
                using var detail = await client.GetAsync($"/api/v1/admin/line/deliveries/{deliveryId:D}", token);
                Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
            }
            using (var missing = await client.GetAsync($"/api/v1/admin/line/deliveries/{Guid.CreateVersion7():D}", token))
                Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var audits = await scope.ServiceProvider.GetRequiredService<OperationAuditQueryService>().QueryLineAsync(
                    new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)), token);
                Assert.IsTrue(audits.Items.Any(item => item.ActorId == session.AccountId));
            }
            session.Role = "Read only";
            using (var denied = await client.GetAsync("/api/v1/admin/line", token))
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using var invalid = await client.PutAsJsonAsync("/internal/v1/line", new LineChannelChange(Guid.Empty, true, null, null, 15), token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        }, context.CancellationToken);

    private sealed class SessionHandler : HttpMessageHandler
    {
        public Guid AccountId { get; } = Guid.CreateVersion7();
        public string Role { get; set; } = "Administrator";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValidatedCookieResponse([
                new(ClaimTypes.NameIdentifier, AccountId.ToString("D"), ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY"),
                new(ClaimTypes.Role, Role, ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY")
            ])) });
    }
}
