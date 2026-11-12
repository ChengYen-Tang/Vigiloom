using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
public sealed class IntegrityChangesTests
{
    [TestMethod]
    public void FirstObservationAndChangedScopeRevisionEstablishBaseline()
    {
        var scope = Guid.CreateVersion7();
        var old = new IntegritySnapshot(scope, 1, true, [new("old", IntegrityItemKind.File, "a", null, null)]);
        var next = new IntegritySnapshot(scope, 2, true, [new("new", IntegrityItemKind.File, "b", null, null)]);
        var initial = IntegrityChanges.Compare(null, old, StringComparer.Ordinal);
        var changedPolicy = IntegrityChanges.Compare(old, next, StringComparer.Ordinal);
        Assert.IsTrue(initial.NewBaseline);
        Assert.IsEmpty(initial.Changes);
        Assert.IsTrue(changedPolicy.NewBaseline);
        Assert.IsEmpty(changedPolicy.Changes);
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChanges.Compare(next, old, StringComparer.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChanges.Compare(old, next with { Complete = false }, StringComparer.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChanges.Compare(old with { Complete = false }, next, StringComparer.Ordinal));
    }

    [TestMethod]
    public void CompleteObservationsReportContentSecurityOwnerAndExistenceChanges()
    {
        var scope = Guid.CreateVersion7();
        var unchanged = new IntegrityItem("same", IntegrityItemKind.File, "a", "acl", "owner");
        var old = new IntegritySnapshot(scope, 1, true, [unchanged, unchanged with { Key = "modified" }, unchanged with { Key = "removed" }]);
        var next = new IntegritySnapshot(scope, 1, true, [unchanged, unchanged with { Key = "modified", SecurityDigest = "changed" }, unchanged with { Key = "added" }]);
        var result = IntegrityChanges.Compare(old, next, StringComparer.Ordinal);
        Assert.IsFalse(result.NewBaseline);
        Assert.HasCount(3, result.Changes);
        Assert.IsTrue(result.Changes.Any(x => x.Kind == IntegrityChangeKind.Modified && x.After?.Key == "modified"));
        Assert.IsTrue(result.Changes.Any(x => x.Kind == IntegrityChangeKind.Added && x.After?.Key == "added"));
        Assert.IsTrue(result.Changes.Any(x => x.Kind == IntegrityChangeKind.Removed && x.Before?.Key == "removed"));
        Assert.HasCount(1, IntegrityChanges.Compare(old with { Entries = [unchanged] }, old with { Entries = [unchanged with { ContentDigest = "different" }] }, StringComparer.Ordinal).Changes);
        Assert.HasCount(1, IntegrityChanges.Compare(old with { Entries = [unchanged] }, old with { Entries = [unchanged with { Owner = "different" }] }, StringComparer.Ordinal).Changes);
        var empty = IntegrityChanges.Compare(old, old with { Entries = [] }, StringComparer.Ordinal);
        Assert.HasCount(3, empty.Changes);
        Assert.IsTrue(empty.Changes.All(x => x.Kind == IntegrityChangeKind.Removed));
    }

    [TestMethod]
    public void ScopeAndKeySemanticsAreExplicit()
    {
        var scope = Guid.CreateVersion7();
        var entry = new IntegrityItem("File", IntegrityItemKind.File, "a", null, null);
        var old = new IntegritySnapshot(scope, 1, true, [entry]);
        var next = new IntegritySnapshot(scope, 1, true, [entry with { Key = "file" }]);
        Assert.IsEmpty(IntegrityChanges.Compare(old, next, StringComparer.OrdinalIgnoreCase).Changes);
        Assert.HasCount(2, IntegrityChanges.Compare(old, next, StringComparer.Ordinal).Changes);
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChanges.Compare(old, next with { ScopeId = Guid.CreateVersion7() }, StringComparer.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChanges.Compare(old, next with { Entries = [entry, entry] }, StringComparer.Ordinal));
    }

    [TestMethod]
    public void RegistryKeys_CompareUnicodeCaseWithoutCollapsingComponentBoundaries()
    {
        var comparer = IntegrityChanges.RegistryKeys;
        var upper = IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["ÄPFEL", "Name"]);
        var lower = IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["äpfel", "name"]);
        Assert.IsTrue(comparer.Equals(upper, lower));
        Assert.AreEqual(comparer.GetHashCode(upper), comparer.GetHashCode(lower));
        var slashInValue = IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["a/b"]);
        var nestedKey = IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["a", "b"]);
        Assert.IsFalse(comparer.Equals(slashInValue, nestedKey));
        var scope = Guid.CreateVersion7();
        var before = new IntegritySnapshot(scope, 1, true, [new(upper, IntegrityItemKind.RegistryValue, "hash", null, null)]);
        var after = new IntegritySnapshot(scope, 1, true, [new(lower, IntegrityItemKind.RegistryValue, "hash", null, null)]);
        Assert.IsEmpty(IntegrityChanges.Compare(before, after, comparer).Changes);
    }
}
