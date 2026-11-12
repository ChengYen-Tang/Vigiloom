using System.Diagnostics;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Processes;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class ProcessReaderTests
{
    [TestMethod]
    public void TryEffectiveUid_UsesSecondStatusField()
    {
        Assert.IsTrue(ProcessReader.TryEffectiveUid("1000\t0\t1000\t1000", out var uid));
        Assert.AreEqual(0U, uid);
        Assert.IsFalse(ProcessReader.TryEffectiveUid("1000", out _));
    }
    [TestMethod]
    public void Read_ContainsCurrentProcessWithStableStartIdentity()
    {
        var snapshot = ProcessReader.Read(CancellationToken.None);
        Assert.IsTrue(snapshot.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
        using var current = Process.GetCurrentProcess();
        var item = snapshot.Items.Single(x => x.ProcessId == current.Id);
        Assert.AreEqual(current.StartTime.ToUniversalTime().Ticks, item.StartedAt.UtcTicks);
        Assert.AreEqual(current.ProcessName, item.Name);
    }

    [TestMethod]
    public void Read_CancelledBeforeEnumeration_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProcessReader.Read(cancellation.Token));
    }
}
