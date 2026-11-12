using Microsoft.Win32;
using Vigiloom.Agent.Platforms.Windows.Integrity;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class RegistryValueReaderTests
{
    [TestMethod]
    public void Read_HashesRawValueAndTypeWithoutExpansion()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var path = $@"Software\VigiloomTests\{Guid.CreateVersion7():N}";
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.CreateSubKey(path, writable: true)!;
            key.SetValue("value", "%TEMP%", RegistryValueKind.ExpandString);
            var first = RegistryValueReader.Read(key.Handle, "value", 1024);
            var repeat = RegistryValueReader.Read(key.Handle, "value", 1024);
            Assert.AreEqual(RegistryValueReadStatus.Success, first.Status);
            Assert.AreEqual(first.Digest, repeat.Digest);
            key.SetValue("value", "%TEMP%", RegistryValueKind.String);
            var differentType = RegistryValueReader.Read(key.Handle, "value", 1024);
            Assert.AreEqual(RegistryValueReadStatus.Success, differentType.Status);
            Assert.AreNotEqual(first.Digest, differentType.Digest);
            Assert.AreEqual(RegistryValueReadStatus.TooLarge, RegistryValueReader.Read(key.Handle, "value", 1).Status);
            Assert.AreEqual(RegistryValueReadStatus.Missing, RegistryValueReader.Read(key.Handle, "missing", 1024).Status);
            key.SetValue("", Array.Empty<byte>(), RegistryValueKind.Binary);
            Assert.AreEqual(RegistryValueReadStatus.Success, RegistryValueReader.Read(key.Handle, "", 0).Status);
        }
        finally
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
