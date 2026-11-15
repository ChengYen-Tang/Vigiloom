using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Hosting.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class InventoryCollectorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void WindowsUpdates_MapsEvidenceAsPartialVersionedSnapshot()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var observation = new Vigiloom.Agent.Platforms.Windows.Inventory.WindowsUpdateObservation(id, 2, "update", true, false,
            ["CVE-2025-1234"], ["1234567"]);
        var result = InventoryCollector.WindowsUpdates(new(InventoryReadStatus.Partial, [observation]));
        Assert.AreEqual(InventoryReadStatus.Partial, result.Status);
        var now = DateTimeOffset.UtcNow;
        var batch = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, InventorySource.WindowsUpdate, now, now, InventoryOutcome.Partial, result.Items).Single();
        Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out var chunk));
        Assert.AreEqual(id, chunk!.Items.Single().UpdateId);
        Assert.AreEqual(2, chunk.Items.Single().UpdateRevision);
        var notInstalled = observation with { UpdateId = Guid.Parse("22222222-2222-4222-8222-222222222222"), Installed = false };
        var observed = InventoryCollector.WindowsUpdates(new(InventoryReadStatus.Partial, [observation, notInstalled]));
        var observedBatch = InventoryChunkBuilder.Build(Guid.CreateVersion7(), 1, InventorySource.WindowsUpdate, now, now, InventoryOutcome.Partial, observed.Items).Single();
        Assert.IsTrue(InventoryChunkCodec.TryRead(observedBatch.Payload, out var observedChunk));
        Assert.IsFalse(observedChunk!.Items.Single(x => x.UpdateId == notInstalled.UpdateId).UpdateInstalled);
        Assert.AreEqual(InventoryReadStatus.Partial, observed.Status);
        Assert.AreEqual(InventoryReadStatus.Partial, InventoryCollector.WindowsUpdates(new(InventoryReadStatus.Partial, [observation, observation])).Status);
        Assert.IsEmpty(InventoryCollector.WindowsUpdates(new(InventoryReadStatus.Partial, [observation, observation])).Items);
    }

    [TestMethod]
    public void Software_PreservesCompleteSourceEvidenceAndDoesNotInventMissingValues()
    {
        var read = new InventoryReadResult<InstalledSoftware>(InventoryReadStatus.Complete,
        [
            new("binary:amd64", "binary", "1.0-1+b1", null, "amd64", "source", "1.0-1"),
            new("other:amd64", "other", "2.0", null, "amd64", "other-source", null)
        ]);
        var result = InventoryCollector.Software(read);
        Assert.AreEqual("source", result.Items[0].SourcePackageName);
        Assert.AreEqual("1.0-1", result.Items[0].SourcePackageVersion);
        Assert.IsNull(result.Items[1].SourcePackageName);
        Assert.IsNull(result.Items[1].SourcePackageVersion);
    }

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
