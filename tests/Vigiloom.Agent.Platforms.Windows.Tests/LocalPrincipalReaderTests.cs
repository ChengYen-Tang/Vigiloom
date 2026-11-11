using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class LocalPrincipalReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void ReadLocalAccountsAndGroups_ReportsPermissionAndValidSids()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows SAM is only available on Windows.");
        var accounts = LocalPrincipalReader.ReadAccounts(TestContext.CancellationToken);
        var groups = LocalPrincipalReader.ReadGroups(TestContext.CancellationToken);
        Assert.IsTrue(accounts.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial or InventoryReadStatus.PermissionDenied);
        Assert.IsTrue(groups.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial or InventoryReadStatus.PermissionDenied);
        foreach (var account in accounts.Items)
        {
            Assert.StartsWith("S-1-", account.Sid);
            Assert.IsNull(account.MemberSids);
        }
        foreach (var group in groups.Items)
        {
            Assert.StartsWith("S-1-", group.Sid);
            Assert.IsNotNull(group.MemberSids);
            foreach (var member in group.MemberSids) Assert.StartsWith("S-1-", member);
        }
    }
}
