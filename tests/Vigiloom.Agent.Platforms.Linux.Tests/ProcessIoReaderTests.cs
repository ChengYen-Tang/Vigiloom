using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Platforms.Linux.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class ProcessIoReaderTests
{
    [TestMethod]
    public void Parse_UsesLogicalTransferCountersAndRejectsMissingOrDuplicateFields()
    {
        Assert.AreEqual(new ProcessIoSnapshot(123, 456), ProcessIoReader.Parse("rchar: 123\nwchar: 456\nread_bytes: 999\n"));
        Assert.IsNull(ProcessIoReader.Parse("rchar: 1\n"));
        Assert.IsNull(ProcessIoReader.Parse("rchar: 1\nrchar: 2\nwchar: 3\n"));
        Assert.IsNull(ProcessIoReader.Parse("rchar: -1\nwchar: 3\n"));
    }

    [TestMethod]
    public async Task ReadAsync_ReadsOwnProcessOnLinux()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var snapshot = await ProcessIoReader.ReadAsync(CancellationToken.None);
        Assert.IsNotNull(snapshot);
    }
}
