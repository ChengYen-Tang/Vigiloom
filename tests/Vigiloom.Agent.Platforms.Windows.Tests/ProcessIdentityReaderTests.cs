using System.Security.Principal;
using Vigiloom.Agent.Core.Processes;
using Vigiloom.Agent.Platforms.Windows.Processes;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class ProcessIdentityReaderTests
{
    [TestMethod]
    public void ReadAccount_VerifiesProcessAndReturnsCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var current = ProcessReader.Read(CancellationToken.None).Items.Single(x => x.ProcessId == Environment.ProcessId);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.AreEqual(identity.User!.Value, ProcessIdentityReader.ReadAccount(current));
        Assert.AreEqual(Environment.ProcessPath, ProcessIdentityReader.Read(current, true).ExecutablePath, ignoreCase: true);
        Assert.IsNull(ProcessIdentityReader.ReadAccount(current with { StartedAt = current.StartedAt.AddSeconds(-1) }));
    }
}
