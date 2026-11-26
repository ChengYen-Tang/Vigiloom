using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
public sealed class HostSecurityObservationTests(TestContext context)
{
    [TestMethod]
    public async Task BaselineReaderUsesRemoteHostObservation()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var report = new BaselineSettingsReport(new string('a', 64), now, now, null);
        var agent = new SecurityAgentObservation(agentId, now, now, 30, null, null, null, null, null, null,
            null, null, null, null, now, JsonSerializer.Serialize(report));
        var reader = new AgentBaselineSyncReader(new Factory(), new UnusedDevices(), new Remote(new(new(id, 1, true), agent)));
        var result = await reader.GetAsync(id, context.CancellationToken);
        Assert.AreEqual(agentId, result!.AgentId);
        Assert.AreEqual(report, result.Report);
    }

    private sealed class Remote(SecurityObservation value) : IHostSecurityObservationReader
    {
        public Task<SecurityObservation?> GetAsync(Guid deviceId, CancellationToken token) => Task.FromResult<SecurityObservation?>(value);
    }

    private sealed class Factory : IDbContextFactory<AgentManagementDbContext>
    {
        public AgentManagementDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer("Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options);
    }

    private sealed class UnusedDevices : IDeviceAgentSource
    {
        public Task<DeviceAgentState?> GetAsync(Guid deviceId, CancellationToken token) => throw new InvalidOperationException("Remote observations must not use the legacy device source.");
        public Task<bool> IsCurrentAsync(Guid deviceId, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) => throw new NotSupportedException();
    }
}
