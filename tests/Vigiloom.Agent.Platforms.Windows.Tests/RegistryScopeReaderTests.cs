using System.Collections.Immutable;
using System.Security.Principal;
using Microsoft.Win32;
using Vigiloom.Agent.Platforms.Windows.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class RegistryScopeReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Read_CollectsRealKeyValuesAndHonorsLimitsAndExclusions()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var path = $@"Software\VigiloomTests\{Guid.CreateVersion7():N}";
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.CreateSubKey(path, writable: true)!;
            key.SetValue("visible", "alpha");
            using (var ignored = key.CreateSubKey("ignored", writable: true)!) ignored.SetValue("secret", "do-not-collect");
            using (var child = key.CreateSubKey("child", writable: true)!) { child.SetValue("visible", "kept"); child.SetValue("__secret", "excluded"); }
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Registry, $@"HKU\{sid}\{path}", true,
                ImmutableArray.Create("ignored", "ignored/**", "**/__*"), 3600, 100, 1048576, 10485760, 16, IntegrityRegistryView.Registry64);
            var result = RegistryScopeReader.Read(scope, TestContext.CancellationToken);
            Assert.AreEqual(IntegrityScanOutcome.Complete, result.Outcome);
            Assert.HasCount(4, result.Items);
            Assert.IsTrue(result.Items.Any(x => x.Kind == IntegrityItemKind.RegistryKey && x.SecurityDigest is not null && x.Owner is not null));
            Assert.IsTrue(result.Items.Any(x => x.Kind == IntegrityItemKind.RegistryValue && x.ContentDigest is { Length: 64 }));
            Assert.IsFalse(result.Items.Any(x => x.Key.Contains("ignored", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.Items.Any(x => x.Key.Contains("child", StringComparison.Ordinal) && x.Key.Contains("visible", StringComparison.Ordinal)));
            Assert.IsFalse(result.Items.Any(x => x.Key.Contains("__secret", StringComparison.Ordinal)));
            var changed = RegistryScopeReader.Read(scope with { MaximumEntries = 1 }, TestContext.CancellationToken);
            Assert.AreEqual(IntegrityScanOutcome.Partial, changed.Outcome);
            Assert.IsTrue(changed.PartialReason!.Value.HasFlag(IntegrityPartialReason.LimitReached));
        }
        finally
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
