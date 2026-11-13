using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;
using Vigiloom.Agent.Platforms.Windows.Integrity;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class WindowsFilesTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OpenChild_UsesDirectoryHandleAndReportsOpenedFileIdentity()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityFileTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var moved = root + "-moved";
        var handles = new List<SafeFileHandle>();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "example.txt"), "hello", TestContext.CancellationToken);
            using var scope = WindowsFiles.OpenScope(root);
            var volume = Path.GetPathRoot(root)!;
            var current = WindowsFiles.OpenVolume(volume);
            handles.Add(current);
            foreach (var component in root[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = WindowsFiles.OpenChild(current, component, ignoreCase: true);
                handles.Add(current);
                var directory = WindowsFiles.ReadStamp(current);
                Assert.AreNotEqual(0u, directory.Attributes & WindowsFiles.Directory);
                Assert.AreEqual(0u, directory.Attributes & WindowsFiles.ReparsePoint);
            }
            Assert.Throws<ArgumentException>(() => WindowsFiles.OpenChild(current, "..\\outside"));
            var names = new List<string>();
            var buffer = new byte[32 * 1024];
            var restart = true;
            while (WindowsFiles.ReadNames(current, buffer, restart) is { } batch)
            {
                names.AddRange(batch);
                restart = false;
            }
            CollectionAssert.Contains(names, "example.txt");
            using var file = WindowsFiles.OpenChild(current, "example.txt");
            var stamp = WindowsFiles.ReadStamp(file);
            Assert.AreEqual(WindowsFiles.ReadStamp(current), WindowsFiles.ReadStamp(scope));
            var security = WindowsFiles.ReadSecurity(file);
            Assert.HasCount(64, security.Digest);
            Assert.StartsWith("S-1-", security.Owner);
            Assert.AreEqual(5UL, stamp.Size);
            Assert.AreEqual(0u, stamp.Attributes & (WindowsFiles.Directory | WindowsFiles.ReparsePoint));
            using var stream = new FileStream(file, FileAccess.Read);
            using var reader = new StreamReader(stream);
            Assert.AreEqual("hello", await reader.ReadToEndAsync(TestContext.CancellationToken));
            reader.Dispose();
            Directory.Move(root, moved);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "example.txt"), "replacement", TestContext.CancellationToken);
            using var afterRename = WindowsFiles.OpenChild(current, "example.txt");
            Assert.AreEqual(stamp, WindowsFiles.ReadStamp(afterRename), "A renamed directory handle must not resolve through its old path.");
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
        }
    }
}
