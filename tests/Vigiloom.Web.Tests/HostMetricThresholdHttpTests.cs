using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class HostMetricThresholdHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Threshold_IsAuthorizedVersionedAndEvaluatesTheExactSeries() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var device = new Device("threshold host", "127.0.0.1", true, now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, 1, new byte[32], now);
        var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
        await using (var db = await agents.CreateDbContextAsync(token))
        {
            db.Agents.Add(agent);
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuCorePercent, "cpu", "0", 80), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuCorePercent, "cpu", "1", 20), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now.AddMinutes(-10), AgentMetricKind.CpuCorePercent, "cpu", "2", 95), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now.AddMinutes(10), AgentMetricKind.CpuCorePercent, "cpu", "3", 95), now));
            await db.SaveChangesAsync(token);
        }
        var url = $"/api/v1/devices/{device.Id:D}/host-metric-threshold?agentId={agent.Id:D}&kind=CpuCorePercent&source=cpu&dimension=0";
        var input = new HostMetricThresholdInput(true, HostMetricComparison.AtLeast, 80, 300, "");
        using var anonymous = PingTestSite.Client(factory);
        using (var denied = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = PingTestSite.Client(factory); await PingTestSite.LoginAsync(reader, "reader", token);
        using var admin = PingTestSite.Client(factory); await PingTestSite.LoginAsync(admin, "admin", token);
        Assert.AreEqual(HostMetricThresholdState.NotConfigured, (await reader.GetFromJsonAsync<HostMetricThresholdStatus>(url, token))!.State);
        using (var denied = await reader.PutAsJsonAsync(url, input, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await admin.PutAsJsonAsync(url, input, token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
        using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
        {
            var json = await anti.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
        }
        HostMetricThresholdView saved;
        using (var response = await admin.PutAsJsonAsync(url, input, token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            saved = (await response.Content.ReadFromJsonAsync<HostMetricThresholdView>(cancellationToken: token))!;
        }
        var status = await reader.GetFromJsonAsync<HostMetricThresholdStatus>(url, token);
        Assert.AreEqual(HostMetricThresholdState.Exceeded, status!.State);
        Assert.AreEqual(80d, status.Sample!.Value);
        Assert.AreEqual(saved.Version, status.Threshold!.Version);
        Assert.AreEqual(HostMetricThresholdState.NotConfigured, (await reader.GetFromJsonAsync<HostMetricThresholdStatus>(url.Replace("dimension=0", "dimension=1"), token))!.State);
        using (var stale = await admin.PutAsJsonAsync(url, input, token)) Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        using (var updated = await admin.PutAsJsonAsync(url, input with { Value = 81, Version = saved.Version }, token)) Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        Assert.AreEqual(HostMetricThresholdState.Normal, (await reader.GetFromJsonAsync<HostMetricThresholdStatus>(url, token))!.State);
        await using (var db = await agents.CreateDbContextAsync(token)) Assert.AreEqual(2, await db.HostMetricThresholdAudits.CountAsync(token));
        foreach (var (dimension, reason) in new[] { ("2", HostMetricUnknownReason.Stale), ("3", HostMetricUnknownReason.FutureTimestamp), ("4", HostMetricUnknownReason.NoSample) })
        {
            var otherUrl = url.Replace("dimension=0", $"dimension={dimension}");
            using var created = await admin.PutAsJsonAsync(otherUrl, input, token);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
            var unknown = await reader.GetFromJsonAsync<HostMetricThresholdStatus>(otherUrl, token);
            Assert.AreEqual(HostMetricThresholdState.Unknown, unknown!.State);
            Assert.AreEqual(reason, unknown.UnknownReason);
        }
        var paddedUrl = url.Replace("source=cpu", "source=cpu%20");
        Assert.AreEqual(HostMetricThresholdState.NotConfigured, (await reader.GetFromJsonAsync<HostMetricThresholdStatus>(paddedUrl, token))!.State);
        var current = (await admin.GetFromJsonAsync<HostMetricThresholdStatus>(url, token))!.Threshold!;
        var concurrent = await Task.WhenAll(
            admin.PutAsJsonAsync(url, input with { Value = 82, Version = current.Version }, token),
            admin.PutAsJsonAsync(url, input with { Value = 83, Version = current.Version }, token));
        try
        {
            CollectionAssert.AreEquivalent(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, concurrent.Select(x => x.StatusCode).ToArray());
        }
        finally { foreach (var response in concurrent) response.Dispose(); }
        var from = Uri.EscapeDataString(now.AddMinutes(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
        var auditUrl = $"/api/v1/operations/audit-feed/host-metric-thresholds?from={from}&to={to}";
        using (var denied = await reader.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        var audit = await admin.GetFromJsonAsync<AuditFeedPage>(auditUrl, token);
        Assert.IsNotNull(audit);
        Assert.IsTrue(audit.Items.Any(x => x.MetricThreshold?.ThresholdId == saved.Id && x.Action == AuditFeedAction.HostMetricThresholdChanged));
        var entry = audit.Items.First(x => x.MetricThreshold?.ThresholdId == saved.Id).MetricThreshold!;
        Assert.AreEqual(agent.Id, entry.AgentId);
        Assert.AreEqual((int)AgentMetricKind.CpuCorePercent, entry.Kind);
        Assert.AreEqual("cpu", entry.Source);
        Assert.AreEqual("0", entry.Dimension);
    }, TestContext.CancellationToken);
}
