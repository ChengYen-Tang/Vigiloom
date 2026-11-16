using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LineWebhookHttpTests(TestContext context)
{
    [TestMethod]
    public Task WebhookRequiresSignatureAndOnlyDiscoversCandidates() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        const string secret = "webhook-test-secret";
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<LineChannel>().SaveAsync(new(false, "test-access-token", secret), token));

        var bytes = Encoding.UTF8.GetBytes("""
            {"destination":"Utest","events":[
              {"type":"join","source":{"type":"group","groupId":"Cfirst"}},
              {"type":"message","source":{"type":"group","groupId":"Csecond"},"message":{"id":"1","type":"text","text":"bind"}}
            ]}
            """);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes));
        using var client = PingTestSite.Client(factory);
        async Task<HttpStatusCode> PostAsync(string header)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook");
            request.Content = new ByteArrayContent(bytes);
            request.Headers.Add("x-line-signature", header);
            using var response = await client.SendAsync(request, token);
            return response.StatusCode;
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, await PostAsync("invalid"));
        Assert.AreEqual(HttpStatusCode.OK, await PostAsync(signature));
        Assert.AreEqual(HttpStatusCode.OK, await PostAsync(signature));
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token);
        var groups = await db.Set<LineGroup>().ToArrayAsync(token);
        Assert.HasCount(2, groups);
        Assert.IsTrue(groups.All(group => !group.Approved));
        CollectionAssert.AreEquivalent(new[] { "Cfirst", "Csecond" }, groups.Select(group => group.ExternalId).ToArray());
    }, context.CancellationToken);
}
