using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostPublicationBoundaryTests(TestContext context)
{
    [TestMethod]
    public Task PingAndAgentStatus_AreReportedIndependently() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("Independent states", "127.0.0.8", true, now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, device.AgentAuthorizationGeneration, new byte[32], now.AddMinutes(-10));
        agent.RecordHeartbeat(now.AddMinutes(-10));
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        { db.Agents.Add(agent); await db.SaveChangesAsync(token); }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            db.PingTargets.Add(new PingTargetState { DeviceId = device.Id, Target = device.Address, DeviceVersion = device.Version,
                Enabled = true, DueAt = now, MeasuredAt = now, State = Reachability.Reachable, Outcome = PingOutcome.Success });
            await db.SaveChangesAsync(token);
        }
        using var reader = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(reader, "reader", token);
        async Task<string> ReadField(string field)
        {
            var document = await new HtmlParser().ParseDocumentAsync(await reader.GetStringAsync($"/devices/{device.Id:D}", token), token);
            var row = document.QuerySelectorAll("dl.device-details div").First(x => x.QuerySelector("dt")?.TextContent.Trim() == field);
            return row.QuerySelector("dd")!.TextContent.Trim();
        }
        Assert.AreEqual("可達", await ReadField("Ping 狀態"));
        Assert.AreEqual("已失聯", await ReadField("Agent 狀態"));
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        { (await db.Agents.SingleAsync(x => x.Id == agent.Id, token)).RecordHeartbeat(now); await db.SaveChangesAsync(token); }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            var target = await db.PingTargets.SingleAsync(x => x.DeviceId == device.Id, token);
            target.State = Reachability.Unreachable; target.Outcome = PingOutcome.Timeout; target.MeasuredAt = now;
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual("不可達", await ReadField("Ping 狀態"));
        Assert.AreEqual("已連線", await ReadField("Agent 狀態"));
        reader.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        Assert.AreEqual("Unreachable", await ReadField("Ping status"));
        Assert.AreEqual("Online", await ReadField("Agent status"));
        var translated = await new HtmlParser().ParseDocumentAsync(await reader.GetStringAsync($"/devices/{device.Id:D}", token), token);
        Assert.Contains("Agent settings synchronization", translated.Body!.TextContent);
        Assert.Contains("Agent local transmission queue", translated.Body.TextContent);
        Assert.Contains("No queue report yet.", translated.Body.TextContent);
        Assert.Contains("No metric loss statistics are available", translated.Body.TextContent);
        Assert.Contains("Central receipt capacity", translated.Body.TextContent);
        Assert.Contains("Independent states", translated.Body.TextContent);
    }, context.CancellationToken);

    [TestMethod]
    public Task PublicPing_DoesNotExposePrivateHostData() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var device = new Device("Internal host", "10.8.7.6", true, now);
        device.SetPublication(true, "Public network", PublicationPolicy.BaselineVersion, now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, device.AgentAuthorizationGeneration, new byte[32], now);
        var batch = new AgentLogBatch(3, Guid.CreateVersion7(), now, Guid.CreateVersion7(), AgentLogSourceKind.Journal,
            "private source", 1, [new AgentLogRecord(Guid.CreateVersion7(), now, "private host log", null, null, null, null, null)]);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Agents.Add(agent); db.HostLogs.Add(new HostLog(agent.Id, batch, 0, now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuTotalPercent, "private.cpu", null, 42), now));
            await db.SaveChangesAsync(token);
        }
        var source = new IntelligenceSource { Key = "private-detection-source", Enabled = true };
        var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "test", RetrievedAt = now,
            Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
        var rule = new SigmaRuleTest { Id = Guid.CreateVersion7(), SourceId = source.Id, RevisionId = revision.Id,
            Path = "rules/private-detection.yml", Digest = new string('a', 64), ActivationVersion = "v1", MatcherVersion = "1",
            SamplesJson = "[]", Passed = true, ActorId = Guid.CreateVersion7(), TestedAt = now };
        var activation = new SigmaRuleActivation { Id = Guid.CreateVersion7(), SourceId = source.Id, Path = rule.Path,
            TestId = rule.Id, Enabled = true, UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now };
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
        {
            db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision); db.SigmaRuleTests.Add(rule); db.SigmaRuleActivations.Add(activation);
            db.SigmaDetectionResults.Add(new SigmaDetectionResult { AgentId = agent.Id, DeviceId = device.Id,
                DeviceGeneration = device.AgentAuthorizationGeneration, MessageId = Guid.CreateVersion7(), ActivationId = activation.Id,
                TestId = rule.Id, ActivationVersion = new byte[8], Result = (int)SigmaExpectedOutcome.Match,
                OccurredAt = now, ReceivedAt = now, EvaluatedAt = now });
            await db.SaveChangesAsync(token);
            var inventory = new InventoryEntry("private-package", "Private software", "1.0");
            var json = System.Text.Json.JsonSerializer.Serialize(inventory);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [security].[InventoryChanges] ([AgentId], [DeviceId], [Source], [Generation], [ObservedAt], [RecordedAt], [Kind], [Key], [BeforeJson], [AfterJson]) VALUES ({agent.Id}, {device.Id}, {(int)InventorySource.LinuxDpkg}, {1L}, {now}, {now}, {(int)InventoryChangeType.Added}, {inventory.Key}, {null}, {json})", token);
        }

        using var anonymous = PingTestSite.Client(factory);
        var publicContent = await anonymous.GetStringAsync($"/api/v1/public/devices/{device.Id:D}", token);
        Assert.Contains("Public network", publicContent);
        Assert.DoesNotContain("Internal host", publicContent);
        Assert.DoesNotContain("10.8.7.6", publicContent);
        Assert.DoesNotContain("private host log", publicContent);
        Assert.DoesNotContain("private.cpu", publicContent);
        Assert.DoesNotContain("private-detection.yml", publicContent);
        Assert.DoesNotContain("Private software", publicContent);
        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var until = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var logs = $"/api/v1/devices/{device.Id:D}/host-logs?from={from}&until={until}";
        var metrics = $"/api/v1/devices/{device.Id:D}/host-metrics?from={from}&until={until}";
        var detections = $"/api/v1/devices/{device.Id:D}/sigma-detections?from={from}&until={until}";
        var inventoryChanges = $"/api/v1/devices/{device.Id:D}/inventory/changes?from={from}&until={until}";
        foreach (var path in new[] { logs, metrics, detections, inventoryChanges,
            $"/api/v1/devices/{device.Id:D}/inventory", $"/api/v1/devices/{device.Id:D}/integrity", $"/api/v1/devices/{device.Id:D}/vulnerabilities" })
        {
            using var denied = await anonymous.GetAsync(path, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode, path);
        }
        using var reader = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(reader, "reader", token);
        var privateContent = await reader.GetStringAsync(logs, token);
        Assert.Contains("private host log", privateContent);
        Assert.Contains("private.cpu", await reader.GetStringAsync(metrics, token));
        Assert.Contains("private-detection.yml", await reader.GetStringAsync(detections, token));
        Assert.Contains("Private software", await reader.GetStringAsync(inventoryChanges, token));
    }, context.CancellationToken);
}
