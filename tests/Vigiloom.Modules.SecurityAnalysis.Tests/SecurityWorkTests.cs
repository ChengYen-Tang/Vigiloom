using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Migrator.Security;
using Vigiloom.Modules.SecurityAnalysis.Domain;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Security;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SecurityWorkTests(TestContext context)
{
    [TestMethod]
    public Task SavesYaraResultBeforeDeletingHostBatchAndDeduplicatesRetry() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await SecurityDatabaseCommand.RunAsync(database.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var agent = Guid.CreateVersion7();
            var device = Guid.CreateVersion7();
            var id = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                db.YaraScans.Add(new YaraScan { Id = id, AgentId = agent, DeviceId = device, RequestedBy = Guid.CreateVersion7(),
                    FilePath = "/tmp/test", Rules = "rule test { condition: true }", MaximumBytes = 1024, TimeoutSeconds = 30, RequestedAt = now });
                await db.SaveChangesAsync(token);
            }
            var payload = YaraResultBatchCodec.Serialize(new(YaraResultBatchCodec.SchemaVersion, id, now,
                new(YaraScanOutcome.Completed, "matched", "", false, now)));
            var fake = new HostHandler(payload);
            using var http = new HttpClient(fake) { BaseAddress = new Uri("http://host/") };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = database.ConnectionString }).Build();
            var consumer = new SecurityWorkHandler(config, new TestClients(http));
            var reference = new HostSecurityBatchReady(agent, id, device, 1, "yara");
            var work = new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(reference), Guid.CreateVersion7().ToString());
            await consumer.ExecuteAsync(work, token);
            await consumer.ExecuteAsync(work, token);
            await using var verify = new SecurityAnalysisDbContext(options);
            var scan = await verify.YaraScans.SingleAsync(token);
            Assert.AreEqual("matched", scan.Output);
            Assert.AreEqual((int)YaraScanOutcome.Completed, scan.Outcome);
            Assert.AreEqual(1, fake.Gets);
            Assert.AreEqual(2, fake.Deletes);
        }, context.CancellationToken);

    private sealed class TestClients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class HostHandler(byte[] payload) : HttpMessageHandler
    {
        public int Gets { get; private set; }
        public int Deletes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) { Gets++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }); }
            Deletes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
