using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
public sealed class InventoryChangesTests
{
    [TestMethod]
    public void Compare_ReportsAdditionsRemovalsAndChangesWithCaseSensitiveKeys()
    {
        InventoryEntry[] previous = [new("A", "old"), new("unchanged", "same"), new("removed", "gone")];
        InventoryEntry[] current = [new("a", "new"), new("A", "updated"), new("unchanged", "same")];

        var changes = InventoryChanges.Compare(previous, current);

        Assert.HasCount(3, changes);
        Assert.IsTrue(changes.Any(x => x.Kind == InventoryChangeKind.Added && x.After?.Key == "a"));
        Assert.IsTrue(changes.Any(x => x.Kind == InventoryChangeKind.Modified && x.Before?.Name == "old" && x.After?.Name == "updated"));
        Assert.IsTrue(changes.Any(x => x.Kind == InventoryChangeKind.Removed && x.Before?.Key == "removed"));
    }

    [TestMethod]
    public void Compare_TreatsGroupMembersAsSetButDistinguishesMissingFromEmpty()
    {
        var previous = new InventoryEntry("group", "group", Members: ["b", "a"]);
        Assert.IsEmpty(InventoryChanges.Compare([previous], [previous with { Members = ["a", "b"] }]));
        Assert.IsEmpty(InventoryChanges.Compare([previous], [previous with { Members = ["a", "b", "a"] }]));
        Assert.HasCount(1, InventoryChanges.Compare([previous], [previous with { Members = ["A", "b"] }]));
        Assert.HasCount(1, InventoryChanges.Compare([previous with { Members = null }], [previous with { Members = [] }]));
        Assert.HasCount(1, InventoryChanges.Compare([previous], [previous with { Members = ["a"] }]));
    }

    [TestMethod]
    public void Compare_RejectsDuplicateKeysInsteadOfSilentlyLosingData()
    {
        InventoryEntry[] duplicate = [new("a", "first"), new("a", "second")];
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChanges.Compare(duplicate, []));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChanges.Compare([], duplicate));
    }

    [TestMethod]
    public void Compare_CompleteEmptySnapshotRemovesPreviousItems()
    {
        var changes = InventoryChanges.Compare([new("a", "a")], []);
        Assert.HasCount(1, changes);
        Assert.AreEqual(InventoryChangeKind.Removed, changes[0].Kind);
    }

    [TestMethod]
    public void Compare_ReportsChangesToEveryPublishedField()
    {
        var original = new InventoryEntry("key", "name");
        InventoryEntry[] revisions =
        [
            original with { Name = "other" }, original with { Version = "1" },
            original with { Architecture = "x64" }, original with { Supplier = "vendor" },
            original with { NumericId = 1 }, original with { PrimaryGroupId = 1 },
            original with { Members = [] }, original with { InstalledOn = "today" },
            original with { OperatingSystem = "os" }, original with { ProcessArchitecture = "x86" },
            original with { Runtime = ".NET" }, original with { AvailableProcessorCount = 2 },
            original with { OperatingSystemFamily = "Linux" }, original with { DistributionId = "fedora" },
            original with { DistributionVersion = "42" }, original with { BootedAt = DateTimeOffset.UtcNow },
            original with { AgentVersion = "1.0" }, original with { TotalMemoryBytes = 1024 },
            original with { ResourceType = "interface" }, original with { ParentKey = "parent" },
            original with { Address = "127.0.0.1" }, original with { CapacityBytes = 4096 },
            original with { HardwareType = "Ethernet" }
        ];
        foreach (var revision in revisions)
        {
            var changes = InventoryChanges.Compare([original], [revision]);
            Assert.HasCount(1, changes);
            Assert.AreEqual(InventoryChangeKind.Modified, changes[0].Kind);
        }
    }
}
