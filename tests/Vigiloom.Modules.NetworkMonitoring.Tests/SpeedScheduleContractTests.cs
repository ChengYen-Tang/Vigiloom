using Hangfire.Common;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

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
    }
}
