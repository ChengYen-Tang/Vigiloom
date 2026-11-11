using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Linux.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class LocalPrincipalReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ParseAccounts_PreservesIdsWithoutPasswordOrPersonalFields()
    {
        using var reader = new StringReader("alice:SECRET-HASH:1000:100:Personal information:/home/alice:/bin/sh\n");
        var result = LocalPrincipalReader.Parse(reader, false, CancellationToken.None);
        Assert.AreEqual(InventoryReadStatus.Complete, result.Status);
        Assert.HasCount(1, result.Items);
        Assert.AreEqual("alice", result.Items[0].Name);
        Assert.AreEqual(1000U, result.Items[0].Id);
        Assert.AreEqual(100U, result.Items[0].PrimaryGroupId);
        Assert.DoesNotContain("SECRET-HASH", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.DoesNotContain("Personal information", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [TestMethod]
    public void ParseGroups_PreservesMembershipAndRejectsMalformedData()
    {
        using var reader = new StringReader("users:x:100:alice,bob\nbroken:x:not-a-gid:alice\n");
        var result = LocalPrincipalReader.Parse(reader, true, CancellationToken.None);
        Assert.AreEqual(InventoryReadStatus.Partial, result.Status);
        Assert.HasCount(1, result.Items);
        CollectionAssert.AreEqual(new[] { "alice", "bob" }, result.Items[0].Members.ToArray());
    }

    [TestMethod]
    public void ParseGroups_MergesSplitGroupsWithoutMergingDifferentIds()
    {
        using var valid = new StringReader("users:x:100:alice,bob\nusers:x:100:bob,charlie\n");
        var complete = LocalPrincipalReader.Parse(valid, true, CancellationToken.None);
        Assert.AreEqual(InventoryReadStatus.Complete, complete.Status);
        Assert.HasCount(1, complete.Items);
        CollectionAssert.AreEquivalent(new[] { "alice", "bob", "charlie" }, complete.Items[0].Members.ToArray());
        using var reader = new StringReader("users:x:100:alice,bob\nusers:x:100:bob,charlie\nusers:x:200:other\n");
        var result = LocalPrincipalReader.Parse(reader, true, CancellationToken.None);
        Assert.AreEqual(InventoryReadStatus.Partial, result.Status);
        Assert.HasCount(1, result.Items);
        CollectionAssert.AreEquivalent(new[] { "alice", "bob", "charlie" }, result.Items[0].Members.ToArray());
        Assert.AreEqual(100U, result.Items[0].Id);
    }

    [TestMethod]
    public void ParseGroups_StopsAtWholeSnapshotMemberLimitAndMarksPartial()
    {
        var text = new System.Text.StringBuilder();
        for (var group = 0; group < 21; group++)
            for (var page = 0; page < 25; page++)
                text.Append("g").Append(group).Append(":x:").Append(group).Append(':')
                    .AppendJoin(',', Enumerable.Range(page * 200, 200).Select(i => $"u{i}" )).Append('\n');
        using var reader = new StringReader(text.ToString());
        var result = LocalPrincipalReader.Parse(reader, true, CancellationToken.None);
        Assert.AreEqual(InventoryReadStatus.Partial, result.Status);
        Assert.AreEqual(InventoryChunkCodec.MaximumSnapshotMembers, result.Items.Sum(x => x.Members.Count));
    }

    [TestMethod]
    public async Task Read_OnLinuxReportsLocalAccountAndGroupSources()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var accounts = await LocalPrincipalReader.ReadAccountsAsync(CancellationToken.None);
        var groups = await LocalPrincipalReader.ReadGroupsAsync(CancellationToken.None);
        Assert.AreEqual(InventoryReadStatus.Complete, accounts.Status);
        Assert.AreEqual(InventoryReadStatus.Complete, groups.Status);
        Assert.IsTrue(accounts.Items.Any(x => x.Id == 0));
        Assert.IsTrue(groups.Items.Any(x => x.Id == 0));
    }

    [TestMethod]
    public async Task FileIdentity_DetectsAtomicReplacementOfAnOpenFile()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var directory = Path.Combine(Path.GetTempPath(), "vigiloom-inventory-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "source");
            var replacement = Path.Combine(directory, "replacement");
            await File.WriteAllTextAsync(path, "first", TestContext.CancellationToken);
            await using var file = File.OpenRead(path);
            Assert.IsTrue(FileIdentity.TryRead(file.SafeFileHandle, path, out _));
            await File.WriteAllTextAsync(replacement, "other", TestContext.CancellationToken);
            File.Move(replacement, path, overwrite: true);
            Assert.IsFalse(FileIdentity.TryRead(file.SafeFileHandle, path, out _));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
