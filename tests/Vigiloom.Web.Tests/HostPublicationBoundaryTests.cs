using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
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
        { db.Agents.Add(agent); db.HostLogs.Add(new HostLog(agent.Id, batch, 0, now)); await db.SaveChangesAsync(token); }

        using var anonymous = PingTestSite.Client(factory);
        var publicContent = await anonymous.GetStringAsync($"/api/v1/public/devices/{device.Id:D}", token);
        Assert.Contains("Public network", publicContent);
        Assert.DoesNotContain("Internal host", publicContent);
        Assert.DoesNotContain("10.8.7.6", publicContent);
        Assert.DoesNotContain("private host log", publicContent);
        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var until = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var logs = $"/api/v1/devices/{device.Id:D}/host-logs?from={from}&until={until}";
        foreach (var path in new[] { logs, $"/api/v1/devices/{device.Id:D}/host-metrics?from={from}&until={until}",
            $"/api/v1/devices/{device.Id:D}/inventory", $"/api/v1/devices/{device.Id:D}/integrity", $"/api/v1/devices/{device.Id:D}/vulnerabilities" })
        {
            using var denied = await anonymous.GetAsync(path, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode, path);
        }
        using var reader = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(reader, "reader", token);
        var privateContent = await reader.GetStringAsync(logs, token);
        Assert.Contains("private host log", privateContent);
    }, context.CancellationToken);
}
