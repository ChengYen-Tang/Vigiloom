using Vigiloom.Updater.Core.Updates;
using System.Runtime.Versioning;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SystemdAgentRuntimeTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task SelectingPreparedVersionReplacesLinkWithoutChangingEitherVersion()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"systemd-{Guid.NewGuid():N}");
        var previous = Path.Combine(root, "previous");
        var target = Path.Combine(root, "staging", "target");
        Directory.CreateDirectory(previous);
        Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(previous, "Vigiloom.Agent"), "old", context.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(target, "Vigiloom.Agent"), "new", context.CancellationToken);
            var current = Path.Combine(root, "current");
            Directory.CreateSymbolicLink(current, previous);

            var runtime = new SystemdAgentRuntime(root);
            Assert.AreEqual(previous, await runtime.GetSelectedDirectoryAsync(context.CancellationToken));
            await runtime.SelectVersionAsync(target, context.CancellationToken);
            Assert.AreEqual(target, await runtime.GetSelectedDirectoryAsync(context.CancellationToken));

            Assert.AreEqual(target, new DirectoryInfo(current).LinkTarget);
            Assert.AreEqual("old", await File.ReadAllTextAsync(Path.Combine(previous, "Vigiloom.Agent"), context.CancellationToken));
            Assert.AreEqual("new", await File.ReadAllTextAsync(Path.Combine(current, "Vigiloom.Agent"), context.CancellationToken));
            Assert.IsEmpty(Directory.GetFileSystemEntries(root, ".current-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task SelectingVersionOutsideInstallationRootDoesNotChangeCurrentLink()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"systemd-{Guid.NewGuid():N}");
        var root = Path.Combine(directory, "install");
        var previous = Path.Combine(root, "previous");
        var outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(previous);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "Vigiloom.Agent"), "untrusted", context.CancellationToken);
            var current = Path.Combine(root, "current");
            Directory.CreateSymbolicLink(current, previous);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                new SystemdAgentRuntime(root).SelectVersionAsync(outside, context.CancellationToken));
            Assert.AreEqual(previous, new DirectoryInfo(current).LinkTarget);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
