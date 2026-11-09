using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class MetricSamplingScheduleTests
{
    [TestMethod]
    public void Schedule_UsesIndependentIntervalsAndRespondsToChanges()
    {
        var clock = new FakeTimeProvider();
        var schedule = new MetricSamplingSchedule(clock);
        var settings = new AgentMetricSettings(30, 10);
        Assert.AreEqual(new MetricSamplingDue(true, true, TimeSpan.Zero), schedule.GetDue(settings));
        schedule.MarkStarted(host: true, process: true);
        Assert.AreEqual(new MetricSamplingDue(false, false, TimeSpan.FromSeconds(10)), schedule.GetDue(settings));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(new MetricSamplingDue(false, true, TimeSpan.Zero), schedule.GetDue(settings));
        schedule.MarkStarted(host: false, process: true);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(new MetricSamplingDue(false, true, TimeSpan.Zero), schedule.GetDue(settings));
        Assert.AreEqual(new MetricSamplingDue(true, true, TimeSpan.Zero), schedule.GetDue(new(15, 10)));
        schedule.MarkStarted(host: true, process: true);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(new MetricSamplingDue(true, true, TimeSpan.Zero), schedule.GetDue(settings));
        schedule.MarkStarted(host: true, process: true);
        Assert.AreEqual(new MetricSamplingDue(false, false, TimeSpan.FromSeconds(10)), schedule.GetDue(settings));
    }
}
