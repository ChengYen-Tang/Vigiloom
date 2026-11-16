using Microsoft.Win32;
using Vigiloom.Agent.Platforms.Windows.Baseline;
using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class NetworkSelectionTests
{
    [TestMethod]
    public void Read_Uses64BitRegistryViewOn64BitWindows()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            Assert.Inconclusive("Requires 64-bit Windows.");
            return;
        }
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var expected = NetworkSelection.ReadValue(root, @"SOFTWARE\Policies\Microsoft\Windows\System");
        var actual = NetworkSelection.Read(20348, "Server", 3);
        Assert.AreEqual(expected.Outcome, actual.Outcome);
        Assert.AreEqual(expected.Evidence, actual.Evidence);
    }

    [TestMethod]
    public void MachineRole_ReadsLocalRole()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var result = MachineRole.Read();
        Assert.IsNotNull(result.Role);
        Assert.IsInRange(0, 5, result.Role.Value);
        Assert.IsFalse(result.PermissionDenied);
    }

    [TestMethod]
    public void ReadValue_DistinguishesExpectedMissingAndWrongType()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var path = $@"Software\VigiloomBaselineTest-{Guid.CreateVersion7():N}";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                Assert.AreEqual(BaselineOutcome.Fail, NetworkSelection.ReadValue(Registry.CurrentUser, path).Outcome);
                key.SetValue("DontDisplayNetworkSelectionUI", "1", RegistryValueKind.String);
                Assert.AreEqual(BaselineOutcome.Fail, NetworkSelection.ReadValue(Registry.CurrentUser, path).Outcome);
                key.SetValue("DontDisplayNetworkSelectionUI", 1, RegistryValueKind.DWord);
                Assert.AreEqual(BaselineOutcome.Pass, NetworkSelection.ReadValue(Registry.CurrentUser, path).Outcome);
                key.SetValue("DontDisplayNetworkSelectionUI", 0, RegistryValueKind.DWord);
                Assert.AreEqual(BaselineOutcome.Fail, NetworkSelection.ReadValue(Registry.CurrentUser, path).Outcome);
            }
            Assert.AreEqual(BaselineOutcome.Unknown, NetworkSelection.Read(null, null, null).Outcome);
            Assert.AreEqual(BaselineOutcome.NotApplicable, NetworkSelection.Read(20348, "Server", 5).Outcome);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}
