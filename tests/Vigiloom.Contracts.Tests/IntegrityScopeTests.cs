using System.Collections.Immutable;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class IntegrityScopeTests
{
    private static IntegrityScope DirectoryScope() => new(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory,
        "/srv/app", true, ImmutableArray.Create("logs/**"), 3600, 10000, 1048576, 10485760, 16);

    [TestMethod]
    public void ValidScopeRequiresExplicitBoundedRootAndPolicy()
    {
        var scope = DirectoryScope();
        Assert.IsTrue(scope.IsValid());
        Assert.IsTrue((scope with { Root = @"C:\Service" }).IsValid());
        Assert.IsFalse((scope with { Root = "../relative" }).IsValid());
        Assert.IsFalse((scope with { Root = "//server/share" }).IsValid());
        Assert.IsFalse((scope with { Root = @"/\server\share" }).IsValid());
        Assert.IsFalse((scope with { Root = @"\\server\share" }).IsValid());
        Assert.IsFalse((scope with { Root = "/srv/../etc" }).IsValid());
        Assert.IsFalse((scope with { Exclusions = ImmutableArray.Create("../secrets") }).IsValid());
        Assert.IsFalse((scope with { MaximumEntries = 20001 }).IsValid());
        Assert.IsFalse((scope with { MaximumScanBytes = 1 }).IsValid());
        Assert.IsFalse((scope with { ScanIntervalSeconds = 0 }).IsValid());
        Assert.IsFalse((scope with { Revision = 0 }).IsValid());
    }

    [TestMethod]
    public void RegistryScopeRequiresExplicitSupportedHiveAndView()
    {
        var scope = DirectoryScope() with { Kind = IntegrityScopeKind.Registry, Root = @"HKLM\Software\Vendor", RegistryView = IntegrityRegistryView.Registry64 };
        Assert.IsTrue(scope.IsValid());
        Assert.IsFalse((scope with { RegistryView = null }).IsValid());
        Assert.IsFalse((scope with { Root = @"HKCU\Software" }).IsValid());
        Assert.IsFalse((scope with { Root = @"HKLM\Software\..\SAM" }).IsValid());
        Assert.IsFalse((scope with { Root = @"HKLM\Software/..\SAM" }).IsValid());
        Assert.IsFalse((DirectoryScope() with { RegistryView = IntegrityRegistryView.Registry32 }).IsValid());
    }

    [TestMethod]
    public void SnapshotRejectsRollbackAndMutationOfAnExistingRevision()
    {
        var first = DirectoryScope();
        var second = first with { Revision = 2, Root = "/srv/next" };
        var initial = IntegritySettingsSnapshot.Create([first], 1);
        var updated = IntegritySettingsSnapshot.Create([second], 2);
        Assert.IsTrue(initial.IsValid());
        Assert.IsTrue(updated.CanFollow(initial));
        Assert.IsFalse(initial.CanFollow(updated));
        Assert.IsFalse(IntegritySettingsSnapshot.Create([first with { Root = "/srv/other" }], 2).CanFollow(initial));
        var removed = IntegritySettingsSnapshot.Create([], 3);
        Assert.IsTrue(removed.CanFollow(updated));
        Assert.IsFalse(initial.CanFollow(removed));
        Assert.IsFalse(IntegritySettingsSnapshot.Create([first], 3).CanFollow(removed));
        Assert.ThrowsExactly<ArgumentException>(() => IntegritySettingsSnapshot.Create([first, first], 1));
        Assert.IsFalse((initial with { Revision = "invalid" }).IsValid());
    }
}
