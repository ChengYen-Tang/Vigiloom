using System.Collections.Immutable;
using Vigiloom.Agent.Platforms.Windows.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class FileScopeReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Read_RejectsLinuxRootOnWindows()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, "/srv/app", true,
            ImmutableArray<string>.Empty, 3600, 100, 1048576, 10485760, 16);
        var result = await FileScopeReader.ReadAsync(scope, TestContext.CancellationToken);
        Assert.AreEqual(IntegrityScanOutcome.Unsupported, result.Outcome);
        Assert.IsEmpty(result.Items);
    }

    [TestMethod]
    public async Task Read_DoesNotFollowDirectoryJunction()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityFileTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var outside = root + "-outside";
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "outside-secret"), "secret", TestContext.CancellationToken);
            var process = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", Path.Combine(root, "junction"), outside }) process.ArgumentList.Add(argument);
            using var command = System.Diagnostics.Process.Start(process) ?? throw new InvalidOperationException("Could not start junction creation.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try { await command.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!command.HasExited) command.Kill(entireProcessTree: true);
                await command.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            Assert.AreEqual(0, command.ExitCode, await command.StandardError.ReadToEndAsync(TestContext.CancellationToken));
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, root, true,
                ImmutableArray<string>.Empty, 3600, 100, 1048576, 10485760, 16);
            var result = await FileScopeReader.ReadAsync(scope, TestContext.CancellationToken);
            Assert.AreEqual(IntegrityScanOutcome.Complete, result.Outcome, $"Reasons: {result.PartialReason}");
            Assert.HasCount(1, result.Items);
            Assert.IsFalse(result.Items.Any(item => item.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(outside).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            var junction = Path.Combine(root, "junction");
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public async Task Read_ScansSelectedFilesAndReportsLimitWithoutPublishingComplete()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityFileTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "child"));
            await File.WriteAllTextAsync(Path.Combine(root, "child", "visible"), "hello", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "child", "__secret"), "ignored", TestContext.CancellationToken);
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, root, true,
                ImmutableArray.Create("**/__*"), 3600, 100, 1048576, 10485760, 16);
            var result = await FileScopeReader.ReadAsync(scope, TestContext.CancellationToken);
            Assert.AreEqual(IntegrityScanOutcome.Complete, result.Outcome, $"Reasons: {result.PartialReason}");
            Assert.HasCount(3, result.Items);
            Assert.IsTrue(result.Items.Any(item => item.Kind == IntegrityItemKind.File && item.ContentDigest is { Length: 64 } && item.SecurityDigest is { Length: 64 }));
            Assert.IsFalse(result.Items.Any(item => item.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)));
            var partial = await FileScopeReader.ReadAsync(scope with { MaximumEntries = 1 }, TestContext.CancellationToken);
            Assert.AreEqual(IntegrityScanOutcome.Partial, partial.Outcome);
            Assert.IsTrue(partial.PartialReason!.Value.HasFlag(IntegrityPartialReason.LimitReached));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
