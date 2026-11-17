using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Hosting;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentModuleFailuresTests
{
    [TestMethod]
    public void OnlyReportsLatestFailureForCurrentAgent()
    {
        var clock = new FakeTimeProvider();
        var failures = new AgentModuleFailures(clock);
        var first = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", "first");
        var second = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", "second");
        failures.Record(first, AgentModule.Metrics);
        clock.Advance(TimeSpan.FromSeconds(1));
        failures.Record(first, AgentModule.Metrics);
        failures.Record(second, AgentModule.Logs);
        var result = failures.ForOwner(first);
        Assert.HasCount(1, result);
        Assert.AreEqual(AgentModule.Metrics, result[0].Module);
        Assert.AreEqual(clock.GetUtcNow(), result[0].OccurredAt);
        Assert.AreEqual(AgentModule.Logs, failures.ForOwner(second).Single().Module);
        Assert.IsEmpty(failures.ForOwner(null));
        Assert.IsTrue(new AgentRuntimeReport("1.0.0", "linux-x64", ModuleFailures: result).IsValid());
        Assert.IsFalse(new AgentRuntimeReport("1.0.0", "linux-x64", ModuleFailures: [result[0], result[0]]).IsValid());
    }
}
