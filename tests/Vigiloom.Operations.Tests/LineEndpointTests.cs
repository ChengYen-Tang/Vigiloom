using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
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
            using var invalid = await client.PutAsJsonAsync("/internal/v1/line", new LineChannelChange(Guid.Empty, true, null, null, 15), token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        }, context.CancellationToken);
}
