extern alias SecurityHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Temporalio.Client;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostLogSecurityIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task OfflineSecurityReceivesHostLogAndDeduplicatesRedelivery()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL_ISOLATED");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL_ISOLATED to a disposable Temporal instance.");
        return SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (securityDb, token) =>
        {
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var logs = new DbContextOptionsBuilder<CentralLogsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory_CentralLogs", "logs")).Options;
            var quota = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            var securityOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(securityDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            await using (var db = new CentralLogsDbContext(logs)) await db.Database.MigrateAsync(token);
            await using (var db = new LogAdmissionDbContext(quota)) await db.Database.MigrateAsync(token);
            foreach (var connection in new[] { operationsDb.ConnectionString, securityDb.ConnectionString })
            {
                var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(connection,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
                await using var db = new SchedulingDbContext(options);
                await db.Database.MigrateAsync(token);
            }
            var now = DateTimeOffset.UtcNow;
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true, IocDetectionEnabled = true, LastSuccessfulCheckAt = now };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = new string('a', 40), RetrievedAt = now,
                Digest = new byte[32], Artifact = [1], SchemaVersion = "1", MappingVersion = "1", LicenseEvidence = "test", Normalized = true };
            var activation = new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = now };
            await using (var db = new SecurityAnalysisDbContext(securityOptions))
            {
                await db.Database.MigrateAsync(token);
                db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision); db.IntelligenceActivations.Add(activation);
                await db.SaveChangesAsync(token);
            }
            var selection = new IocSourceSnapshot(IocSourceAvailability.Ready, source.Id, revision.Id, Convert.ToBase64String(activation.Version), revision.UpstreamVersion, "test", 1, 0);
            var queue = "vigiloom-hostlogs-test-" + Guid.CreateVersion7().ToString("N");
            using var operationsHost = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:EnableOperationsWorker", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString, ["Temporal:Address"] = address, ["Work:SecurityLogQueue"] = queue }));
            });
            using var operationsClient = operationsHost.CreateClient();
            const string xml = """
                <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><EventID>3</EventID><Channel>Microsoft-Windows-Sysmon/Operational</Channel></System><EventData><Data Name="DestinationIp">192.0.2.1</Data></EventData></Event>
                """;
            var item = new AgentLogRecord(Guid.CreateVersion7(), now, xml, null, 3, null, null, null);
            var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent, "Sysmon", 1, [item]);
            var submission = new HostLogSubmission(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, batch);
            using (var response = await operationsClient.PostAsJsonAsync("/internal/v1/host-logs", submission, token))
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var temporal = await TemporalClient.ConnectAsync(new(address));
            var client = new ServiceWorkClient(temporal);
            Guid deliveryId;
            string payload;
            var opsScheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operationsDb.ConnectionString).Options;
            await using (var db = new SchedulingDbContext(opsScheduling))
            {
                deliveryId = await db.Database.SqlQuery<Guid>($"SELECT MessageId AS [Value] FROM scheduling.Outbox WHERE MessageType = {TemporalOutboxHandler.Type}").SingleAsync(token);
                payload = await db.Database.SqlQuery<string>($"SELECT Payload AS [Value] FROM scheduling.Outbox WHERE MessageId = {deliveryId}").SingleAsync(token);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                while (await db.Database.SqlQuery<int>($"SELECT State AS [Value] FROM scheduling.Outbox WHERE MessageId = {deliveryId}").SingleAsync(token) != 2)
                {
                    if (DateTimeOffset.UtcNow >= deadline) Assert.Fail("Host log was not delivered to Temporal.");
                    await Task.Delay(250, token);
                }
            }
            using var securityHost = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Work:SecurityQueue", queue + "-agent");
                web.UseSetting("Work:SecurityLogQueue", queue);
                web.UseSetting("Work:OperationsQueue", queue + "-operations");
                web.UseSetting("Work:AuditQueue", queue + "-audit");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Security"] = securityDb.ConnectionString, ["Temporal:Address"] = address,
                    ["Work:SecurityQueue"] = queue + "-agent", ["Work:SecurityLogQueue"] = queue }));
                web.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IIocSource>();
                    services.AddSingleton<IIocSource>(new Source(selection));
                    services.AddHttpClient(nameof(IHostLogReader)).ConfigurePrimaryHttpMessageHandler(() => operationsHost.Server.CreateHandler());
                });
            });
            using var securityClient = securityHost.CreateClient();
            await client.Get(deliveryId.ToString("D")).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(60), token);
            var request = JsonSerializer.Deserialize<TemporalWorkRequest>(payload)!;
            var replay = await client.StartAsync(Guid.CreateVersion7().ToString("D"), queue, request.Work with { DeliveryId = deliveryId.ToString("D") }, token);
            await replay.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(60), token);
            await using var check = new SecurityAnalysisDbContext(securityOptions);
            var detection = await check.IocDetections.SingleAsync(token);
            Assert.AreEqual(item.MessageId, detection.MessageId);
            Assert.AreEqual("192.0.2.1", detection.Value);
            Assert.AreEqual(1L, await check.Database.SqlQueryRaw<long>("SELECT COUNT_BIG(*) AS [Value] FROM scheduling.WorkReceipts").SingleAsync(token));
        }, outerToken), context.CancellationToken);
    }

    private sealed class Source(IocSourceSnapshot selection) : IIocSource
    {
        public Task<IocSourceSnapshot> GetActiveAsync(CancellationToken token) => Task.FromResult(selection);
        public Task<IocSourceFile> ReadFileAsync(IocSourceSnapshot snapshot, string path, CancellationToken token) =>
            Task.FromResult(new IocSourceFile(IocSourceAvailability.Ready, path, null, null, null, null, null,
                path.Contains("c2", StringComparison.Ordinal) ? "192.0.2.1"u8.ToArray() : []));
    }
}
