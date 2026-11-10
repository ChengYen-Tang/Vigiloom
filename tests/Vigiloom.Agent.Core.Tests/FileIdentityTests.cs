using Vigiloom.Agent.Core.Logs;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class FileIdentityTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Identity_TracksOpenFileAcrossAppendAndRenameButDistinguishesReplacement()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Windows or Linux."); return; }
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-log-identity-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "active.log");
        var rotated = Path.Combine(directory, "rotated.log");
        try
        {
            await File.WriteAllTextAsync(path, "first\n", TestContext.CancellationToken);
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var identity = FileIdentity.FromHandle(original.SafeFileHandle);
            await File.AppendAllTextAsync(path, "second\n", TestContext.CancellationToken);
            Assert.AreEqual(identity, FileIdentity.FromHandle(original.SafeFileHandle));
            File.Move(path, rotated);
            await File.WriteAllTextAsync(path, "replacement\n", TestContext.CancellationToken);
            using var replacement = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.AreEqual(identity, FileIdentity.FromHandle(original.SafeFileHandle));
            Assert.AreNotEqual(identity, FileIdentity.FromHandle(replacement.SafeFileHandle));
        }
        finally
        {
            File.Delete(path);
            File.Delete(rotated);
            Directory.Delete(directory);
        }
    }
}
