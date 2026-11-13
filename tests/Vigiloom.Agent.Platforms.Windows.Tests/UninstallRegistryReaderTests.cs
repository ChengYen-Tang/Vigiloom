using Microsoft.Win32;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class UninstallRegistryReaderTests
{
    [TestMethod]
    public void ReadKey_SeparatesExplicitRegistryViews()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem) { Assert.Inconclusive("Requires 64-bit Windows."); return; }
        if (Environment.GetEnvironmentVariable("VIGILOOM_EXPECT_X86_TESTHOST") == "1")
        {
            Assert.IsFalse(Environment.Is64BitProcess);
            Assert.StartsWith(".NET 10.", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        }
        var path = $@"Software\Classes\CLSID\{{{Guid.CreateVersion7():D}}}";
        using var base32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
        using var base64 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using (var parent = base32.CreateSubKey(path))
            using (var entry = parent.CreateSubKey("package32")) entry.SetValue("DisplayName", "32 view package");
            using (var parent = base64.CreateSubKey(path))
            using (var entry = parent.CreateSubKey("package64")) entry.SetValue("DisplayName", "64 view package");
            using var view32 = base32.OpenSubKey(path) ?? throw new InvalidOperationException("32-bit view missing.");
            using var view64 = base64.OpenSubKey(path) ?? throw new InvalidOperationException("64-bit view missing.");
            var result32 = UninstallRegistryReader.ReadKey(view32, CancellationToken.None);
            var result64 = UninstallRegistryReader.ReadKey(view64, CancellationToken.None);
            Assert.AreEqual(InventoryReadStatus.Complete, result32.Status);
            Assert.AreEqual(InventoryReadStatus.Complete, result64.Status);
            Assert.AreEqual("32 view package", result32.Items.Single().Name);
            Assert.AreEqual("64 view package", result64.Items.Single().Name);
            var machine32 = UninstallRegistryReader.Read(RegistryView.Registry32, CancellationToken.None);
            var machine64 = UninstallRegistryReader.Read(RegistryView.Registry64, CancellationToken.None);
            Assert.IsTrue(machine32.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
            Assert.IsTrue(machine64.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
        }
        finally
        {
            var errors = new List<Exception>();
            try { base32.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch (Exception ex) { errors.Add(ex); }
            try { base64.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("Could not clean up the registry test fixture.", errors);
        }
    }

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
