extern alias OperationsHost;

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
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Operations;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RemoteLineIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task OperationsReceivesSignedWebhookAndStoresSettings() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
            using var host = new WebApplicationFactory<OperationsHost::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Hangfire:Enabled", "false");
                builder.UseSetting("Temporal:EnableOperationsWorker", "false");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = database.ConnectionString }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDataProtectionProvider>();
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    foreach (var item in services.Where(item => item.ImplementationType?.Name is "WorkDispatcher" or "LineAggregationWorker").ToArray()) services.Remove(item);
                });
            });
            using var client = host.CreateClient();
            var remote = new RemoteLine(client);
            Assert.IsTrue(await remote.SaveAsync(new LineChannelUpdate(true, "token", "secret", 25), Guid.CreateVersion7(), token));
            var payload = Encoding.UTF8.GetBytes("{\"destination\":\"Utest\",\"events\":[{\"type\":\"join\",\"source\":{\"type\":\"group\",\"groupId\":\"Cnew\"}}]}");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook");
            request.Content = new ByteArrayContent(payload);
            request.Headers.Add("x-line-signature", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), payload)));
            using var response = await client.SendAsync(request, token);
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            var settings = await remote.GetAsync(token);
            Assert.AreEqual(25, settings.AggregationSeconds);
            Assert.AreEqual("Cnew", Assert.ContainsSingle(settings.Groups).ExternalId);
            Assert.IsFalse(settings.Groups[0].Approved);
        }, context.CancellationToken);
}
