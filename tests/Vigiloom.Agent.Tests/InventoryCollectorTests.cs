using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Hosting.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class InventoryCollectorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Processes_CollectsPathOnlyWhenEnabledAndNeverIncludesArguments()
    {
        var collector = new InventoryCollector(TimeProvider.System);
        var disabled = await collector.ReadAsync(InventorySource.HostProcesses, TestContext.CancellationToken);
        Assert.IsTrue(disabled.Items.All(x => x.ExecutablePath is null && x.CommandLine is null));
        var enabled = await collector.ReadAsync(InventorySource.HostProcesses, TestContext.CancellationToken, true);
        var current = enabled.Items.Single(x => x.NumericId == (uint)Environment.ProcessId);
        Assert.IsNotNull(current.ExecutablePath);
        Assert.AreEqual(current.ExecutablePath + InventoryChunkCodec.MaskedArgumentsSuffix, current.CommandLine);
        Assert.IsTrue(enabled.Items.All(x => x.CommandLine is null || x.CommandLine == x.ExecutablePath + InventoryChunkCodec.MaskedArgumentsSuffix));
        var now = DateTimeOffset.UtcNow;
        var batches = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, InventorySource.HostProcesses, now, now,
            (InventoryOutcome)enabled.Status, enabled.Items);
        foreach (var batch in batches) Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out _));
    }

    [TestMethod]
    [DataRow("1.2.3-rc.1+abc123", "1.2.3-rc.1")]
    [DataRow("1.2.3-rc.2+abc123", "1.2.3-rc.2")]
    [DataRow("1.2.3+abc123", "1.2.3")]
    public void AgentVersion_PreservesPrereleaseButDropsBuildMetadata(string value, string expected) =>
        Assert.AreEqual(expected, InventoryCollector.NormalizeAgentVersion(value));

    [TestMethod]
    public async Task Host_MapsIntoAValidInventoryChunk()
    {
        var collector = new InventoryCollector(TimeProvider.System);
        var read = await collector.ReadAsync(InventorySource.HostSystem, TestContext.CancellationToken);
        Assert.AreEqual(InventoryReadStatus.Complete, read.Status);
        Assert.HasCount(1, read.Items);
        Assert.IsNotNull(read.Items[0].OperatingSystem);
        Assert.IsNotNull(read.Items[0].Architecture);
        Assert.IsNotNull(read.Items[0].ProcessArchitecture);
        Assert.IsNotNull(read.Items[0].OperatingSystemFamily);
        Assert.IsNotNull(read.Items[0].BootedAt);
        Assert.IsNotNull(read.Items[0].AgentVersion);
        Assert.IsNotNull(read.Items[0].TotalMemoryBytes);
        var now = DateTimeOffset.UtcNow;
        var batches = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, InventorySource.HostSystem, now, now,
            InventoryOutcome.Complete, read.Items);
        Assert.IsTrue(InventoryChunkCodec.TryRead(batches[0].Payload, out _));
    }

    [TestMethod]
    public async Task WrongPlatformSource_IsUnsupportedInsteadOfCompleteEmpty()
    {
        var collector = new InventoryCollector(TimeProvider.System);
        var other = OperatingSystem.IsWindows() ? InventorySource.LinuxDpkg : InventorySource.WindowsQuickFix;
        var read = await collector.ReadAsync(other, TestContext.CancellationToken);
        Assert.AreEqual(InventoryReadStatus.Unsupported, read.Status);
        Assert.IsEmpty(read.Items);
    }

    [TestMethod]
    public async Task LocalVolumes_UsesSupportedOperatingSystemCollector()
    {
        var collector = new InventoryCollector(TimeProvider.System);
        var read = await collector.ReadAsync(InventorySource.HostVolumes, TestContext.CancellationToken);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Assert.AreEqual(InventoryReadStatus.Unsupported, read.Status);
            return;
        }
        Assert.IsTrue(read.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
        Assert.IsTrue(read.Items.All(x => x.ResourceType == "volume" && x.CapacityBytes is > 0));
        var now = DateTimeOffset.UtcNow;
        var batches = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, InventorySource.HostVolumes, now, now,
            (InventoryOutcome)read.Status, read.Items);
        foreach (var batch in batches) Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out _));
    }
}
