using Vigiloom.Agent.Platforms.Linux.Inventory;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class DpkgReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("ii ")]
    [DataRow("hi ")]
    public void ParseLine_PreservesInstalledAndHeldPackages(string status)
    {
        var (result, item) = DpkgReader.Parse($"{status}\tlibfoo:amd64\t1:2.3-4\tamd64\tExample maintainer");
        Assert.AreEqual(PackageCommand.LineStatus.Installed, result);
        Assert.IsNotNull(item);
        Assert.AreEqual("libfoo:amd64", item.SourceKey);
        Assert.AreEqual("1:2.3-4", item.Version);
        Assert.AreEqual("amd64", item.Architecture);
    }

    [TestMethod]
    public void ParseLine_DistinguishesRemovedFromMalformed()
    {
        Assert.AreEqual(PackageCommand.LineStatus.NotInstalled, DpkgReader.Parse("rc \tfoo\t1\tamd64\t").Status);
        Assert.AreEqual(PackageCommand.LineStatus.Invalid, DpkgReader.Parse("ii \tfoo\t\tamd64\t").Status);
        Assert.AreEqual(PackageCommand.LineStatus.Invalid, DpkgReader.Parse("iiR\tfoo\t1\tamd64\t").Status);
        Assert.AreEqual(PackageCommand.LineStatus.Invalid, DpkgReader.Parse("iU \tfoo\t1\tamd64\t").Status);
    }

    [TestMethod]
    public async Task ReadRows_ReportsOversizedRowWithoutLosingOtherPackages()
    {
        using var reader = new StringReader($"ii \tfoo\t1\tamd64\tmaintainer\n{new string('x', 20_000)}\nii \tbar\t2\tall\t\n");
        var items = new List<InstalledSoftware>();
        var result = await PackageCommand.ReadRowsAsync(reader, items, DpkgReader.Parse, CancellationToken.None);
        Assert.IsTrue(result.Partial);
        Assert.IsFalse(result.Exceeded);
        Assert.HasCount(2, items);
        Assert.AreEqual("foo", items[0].Name);
        Assert.AreEqual("bar", items[1].Name);
    }

    [TestMethod]
    public async Task ReadRows_StopsWhenTotalOutputExceedsLimit()
    {
        using var reader = new RepeatedReader(32 * 1024 * 1024 + 1);
        var result = await PackageCommand.ReadRowsAsync(reader, [], DpkgReader.Parse, CancellationToken.None);
        Assert.IsTrue(result.Partial);
        Assert.IsTrue(result.Exceeded);
    }

    [TestMethod]
    public async Task PackageCommand_InvalidUtf8CannotBecomeCompleteInventory()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        if (!File.Exists("/usr/bin/printf")) { Assert.Inconclusive("Requires /usr/bin/printf."); return; }
        var result = await PackageCommand.ReadAsync("/usr/bin/printf", ["\\377"], DpkgReader.Parse, CancellationToken.None);
        Assert.AreEqual(InventoryReadStatus.Error, result.Status);
        Assert.IsEmpty(result.Items);
    }

    [TestMethod]
    public async Task PackageCommand_MissingExecutableIsUnsupported()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var missing = Path.Combine(Path.GetTempPath(), "vigiloom-missing-" + Guid.CreateVersion7().ToString("N"));
        var result = await PackageCommand.ReadAsync(missing, [], DpkgReader.Parse, TestContext.CancellationToken);
        Assert.AreEqual(InventoryReadStatus.Unsupported, result.Status);
    }

    private sealed class RepeatedReader(int remaining) : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(remaining, buffer.Length);
            buffer.Span[..count].Fill('x');
            remaining -= count;
            return ValueTask.FromResult(count);
        }
    }

    [TestMethod]
    public async Task ReadAsync_OnLinuxReportsInstalledPackagesOrUnsupported()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var result = await DpkgReader.ReadAsync(CancellationToken.None);
        if (!File.Exists("/usr/bin/dpkg-query")) Assert.AreEqual(InventoryReadStatus.Unsupported, result.Status);
        else
        {
            Assert.AreEqual(InventoryReadStatus.Complete, result.Status);
            Assert.IsNotEmpty(result.Items);
        }
    }
}
