using Vigiloom.Agent.Platforms.Linux.Baseline;
using Vigiloom.Contracts.Agent.Baseline;
using System.Diagnostics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class ShadowPermissionsTests
{
    [TestMethod]
    public void Read_InaccessibleMetadataReportsPermissionDenied()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") { Assert.Inconclusive("Requires an unprivileged Linux user."); return; }
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-baseline-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "shadow");
            File.WriteAllText(path, "private");
            File.SetUnixFileMode(directory, 0);
            Assert.AreEqual(BaselineOutcome.PermissionDenied, ShadowPermissions.Read("ubuntu", "24.04", path).Outcome);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Read_ExtendedAclIsAFinding()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        if (!File.Exists("/usr/bin/setfacl") && !File.Exists("/usr/sbin/setfacl")) { Assert.Inconclusive("Requires setfacl."); return; }
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-baseline-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "shadow");
            File.WriteAllText(path, "private");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var process = Process.Start(new ProcessStartInfo("setfacl") { ArgumentList = { "-m", "u:65534:r--", path } });
            Assert.IsNotNull(process);
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode);
            var result = ShadowPermissions.Read("ubuntu", "24.04", path);
            Assert.AreEqual(BaselineOutcome.Fail, result.Outcome);
            Assert.Contains("extended ACL present", result.Evidence!);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void Read_UsesModeAndRejectsSymlinkWithoutReadingContents()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-baseline-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "shadow");
            File.WriteAllText(path, "test content is not evidence");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var secure = ShadowPermissions.Read("ubuntu", "24.04", path);
            Assert.AreEqual(BaselineOutcome.Pass, secure.Outcome);
            Assert.AreEqual(BaselineOutcome.Fail, ShadowPermissions.ReadMode(path, UnixFileMode.UserRead).Outcome);
            Assert.AreEqual(BaselineOutcome.Pass, ShadowPermissions.ReadMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite).Outcome);
            Assert.DoesNotContain("test content", secure.Evidence!);

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Assert.AreEqual(BaselineOutcome.Fail, ShadowPermissions.Read("ubuntu", "24.04", path).Outcome);
            var link = Path.Combine(directory, "link");
            File.CreateSymbolicLink(link, path);
            Assert.AreEqual(BaselineOutcome.Unknown, ShadowPermissions.Read("ubuntu", "24.04", link).Outcome);
            var fifo = Path.Combine(directory, "fifo");
            using var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { "-m", "600", fifo } });
            Assert.IsNotNull(process);
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode);
            Assert.AreEqual(BaselineOutcome.Unknown, ShadowPermissions.Read("ubuntu", "24.04", fifo).Outcome);
            Assert.AreEqual(BaselineOutcome.NotApplicable, ShadowPermissions.Read("ubuntu", "20.04", path).Outcome);
            Assert.AreEqual(BaselineOutcome.Unknown, ShadowPermissions.Read(null, null, path).Outcome);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
