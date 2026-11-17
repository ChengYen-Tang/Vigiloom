using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class AgentRuntimeReportTests
{
    [TestMethod]
    public void HeartbeatRuntime_AcceptsSupportedReportsAndRejectsMalformedValues()
    {
        var epoch = Guid.CreateVersion7();
        Assert.IsTrue(new AgentHeartbeatReport(1, epoch, 1, null, Runtime: new("1.2.3", "win-x64")).IsValid());
        Assert.IsTrue(new AgentHeartbeatReport(1, epoch, 1, null).IsValid());
        Assert.IsFalse(new AgentHeartbeatReport(1, epoch, 1, null, Runtime: new("1.2.3\n", "win-x64")).IsValid());
        Assert.IsFalse(new AgentHeartbeatReport(1, epoch, 1, null, Runtime: new(new string('a', 129), "win-x64")).IsValid());
        Assert.IsFalse(new AgentHeartbeatReport(1, epoch, 1, null, Runtime: new("1.2.3", "linux-x86")).IsValid());
    }
}
