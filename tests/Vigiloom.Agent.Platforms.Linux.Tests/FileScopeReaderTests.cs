using System.Collections.Immutable;
using Vigiloom.Agent.Platforms.Linux.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class FileScopeReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void OpenChild_InspectsFinalSymlinkButRejectsIntermediateSymlink()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var root = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityFileTests", Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), "/etc");
            using var directory = LinuxFiles.OpenRoot(root, out var error);
            Assert.IsNotNull(directory, $"Open root failed: {error}");
            using var leaf = LinuxFiles.OpenChild(directory, "link", false, true, out error);
            Assert.IsNotNull(leaf, $"Inspect link failed: {error}");
            Assert.IsTrue(LinuxFiles.TryStamp(leaf, out var stamp, out error));
            Assert.AreEqual(LinuxFiles.SymlinkType, (ushort)(stamp.Mode & LinuxFiles.TypeMask));
            using var escaped = LinuxFiles.OpenChild(directory, "link/passwd", false, true, out error);
            Assert.IsNull(escaped);
            Assert.AreEqual(40, error); // ELOOP: intermediate symlink is not a deliberate leaf exclusion.
            var child = Path.Combine(root, "child");
            Directory.CreateDirectory(child);
            using var observed = LinuxFiles.OpenChild(directory, "child", true, false, out error);
            Assert.IsNotNull(observed);
            Assert.IsTrue(LinuxFiles.TryStamp(observed, out var original, out error));
            Assert.IsTrue(LinuxFiles.DirectoryMatches(directory, "child", original));
            Directory.Move(child, Path.Combine(root, "old-child"));
            Directory.CreateDirectory(child);
            Assert.IsFalse(LinuxFiles.DirectoryMatches(directory, "child", original));
        }
        finally
        {
            var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityFileTests");
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Read_UsesLocalScopeHonorsExclusionsAndNeverFollowsSymlinks()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityFileTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "child"));
            await File.WriteAllTextAsync(Path.Combine(root, "child", "visible"), "hello", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "child", "__secret"), "ignored", TestContext.CancellationToken);
            File.CreateSymbolicLink(Path.Combine(root, "escape"), "/etc/passwd");
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, root, true,
                ImmutableArray.Create("**/__*"), 3600, 100, 1048576, 10485760, 16);
            var result = await FileScopeReader.ReadAsync(scope, TestContext.CancellationToken);
            Assert.AreEqual(IntegrityScanOutcome.Complete, result.Outcome, $"Reasons: {result.PartialReason}; entries: {string.Join(",", result.Items.Select(x => x.Key))}");
            Assert.HasCount(3, result.Items);
            Assert.IsTrue(result.Items.Any(x => x.Kind == IntegrityItemKind.File && x.ContentDigest is { Length: 64 } && x.SecurityDigest is { Length: 64 }));
            Assert.IsFalse(result.Items.Any(x => x.Key.Contains("secret", StringComparison.Ordinal) || x.Key.Contains("escape", StringComparison.Ordinal)));
            var partial = await FileScopeReader.ReadAsync(scope with { MaximumEntries = 1 }, TestContext.CancellationToken);
            Assert.AreEqual(IntegrityScanOutcome.Partial, partial.Outcome);
            Assert.IsTrue(partial.PartialReason!.Value.HasFlag(IntegrityPartialReason.LimitReached));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
