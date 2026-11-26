using Hangfire.Common;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedScheduleContractTests
{
    [TestMethod]
    public void RecurringJobUsesSharedModuleAndNetworkQueue()
    {
        var job = Job.FromExpression<NetworkAutomaticSpeedJob>(worker => worker.RunAsync(CancellationToken.None), "network");
        Assert.AreEqual(typeof(SpeedQueue).Assembly, job.Type.Assembly);
        Assert.AreEqual("network", job.Queue);
        var maintenance = new[]
        {
            Job.FromExpression<NetworkPingRetentionJob>(worker => worker.RunAsync(CancellationToken.None), "network"),
            Job.FromExpression<NetworkPingRollupJob>(worker => worker.RunAsync(CancellationToken.None), "network"),
            Job.FromExpression<NetworkSpeedRetentionJob>(worker => worker.RunAsync(CancellationToken.None), "network")
        };
        foreach (var item in maintenance)
        {
            Assert.AreEqual(typeof(SpeedQueue).Assembly, item.Type.Assembly);
            Assert.AreEqual("network", item.Queue);
        }
    }
}
