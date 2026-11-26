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
        var job = Job.FromExpression<AutomaticSpeedJob>(worker => worker.RunAsync(CancellationToken.None), "network");
        Assert.AreEqual(typeof(SpeedQueue).Assembly, job.Type.Assembly);
        Assert.AreEqual("network", job.Queue);
        var maintenance = new[]
        {
            Job.FromExpression<PingRetentionJob>(worker => worker.RunAsync(CancellationToken.None), "network"),
            Job.FromExpression<PingRollupBackfillJob>(worker => worker.RunAsync(CancellationToken.None), "network"),
            Job.FromExpression<SpeedRetentionJob>(worker => worker.RunAsync(CancellationToken.None), "network")
        };
        foreach (var item in maintenance)
        {
            Assert.AreEqual(typeof(SpeedQueue).Assembly, item.Type.Assembly);
            Assert.AreEqual("network", item.Queue);
        }
    }
}
