using Microsoft.Win32;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class UninstallRegistryReaderTests
{
    [TestMethod]
    public void ReadKey_DistinguishesCompleteEmptyAndPartialResults()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var path = $@"Software\VigiloomTests\{Guid.CreateVersion7():D}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            var empty = UninstallRegistryReader.ReadKey(key, CancellationToken.None);
            Assert.AreEqual(InventoryReadStatus.Complete, empty.Status);
            Assert.IsEmpty(empty.Items);
            using (var package = key.CreateSubKey("Example"))
            {
                package.SetValue("DisplayName", "Example app");
                package.SetValue("DisplayVersion", "1.2.3+build");
                package.SetValue("Publisher", "Example publisher");
            }
            using (var oversized = key.CreateSubKey("Oversized")) oversized.SetValue("DisplayName", new string('x', 2049));
            var result = UninstallRegistryReader.ReadKey(key, CancellationToken.None);
            Assert.AreEqual(InventoryReadStatus.Partial, result.Status);
            Assert.HasCount(1, result.Items);
            Assert.AreEqual(new InstalledSoftware("Example", "Example app", "1.2.3+build", "Example publisher"), result.Items[0]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
            {
                if (OperatingSystem.IsWindows()) UninstallRegistryReader.ReadKey(key, cancellation.Token);
            });
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [TestMethod]
    public void ReadKey_MalformedValueCannotBeReportedAsComplete()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var path = $@"Software\VigiloomTests\{Guid.CreateVersion7():D}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            using (var malformed = key.CreateSubKey("Malformed")) malformed.SetValue("DisplayName", 42, RegistryValueKind.DWord);
            var result = UninstallRegistryReader.ReadKey(key, CancellationToken.None);
            Assert.AreEqual(InventoryReadStatus.Partial, result.Status);
            Assert.IsEmpty(result.Items);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [TestMethod]
    public void RegistryStamp_TracksSubkeyChanges()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var path = $@"Software\VigiloomTests\{Guid.CreateVersion7():D}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            Assert.IsTrue(RegistryRead.Stamp(key.Handle, out var before, out _));
            using (key.CreateSubKey("Example")) { }
            Assert.IsTrue(RegistryRead.Stamp(key.Handle, out var after, out _));
            Assert.AreEqual(before + 1, after);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [TestMethod]
    public void Read_RejectsAnImplicitRegistryView()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (OperatingSystem.IsWindows()) UninstallRegistryReader.Read(RegistryView.Default, CancellationToken.None);
        });
    }
}
