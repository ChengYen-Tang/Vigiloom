using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentMetricSettingsRuntimeTests
{
    [TestMethod]
    public void Runtime_RetainsLastGoodForOwnerAndRejectsStaleOwnerUpdates()
    {
        var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://one.test/", "state");
        var next = new AgentSettingsOwner(Guid.CreateVersion7(), "https://two.test/", "state");
        var runtime = new AgentMetricSettingsRuntime();
        runtime.Bind(owner);
        var saved = new AgentSavedMetricSettings(AgentMetricSettingsSnapshot.Create(new(45, 90, 1500, 768, 3500)), DateTimeOffset.UtcNow,
            Guid.CreateVersion7(), owner.AgentId, owner.Server);
        runtime.Publish(owner, saved);
        Assert.AreEqual(new AgentMetricSettings(45, 90, 1500, 768, 3500), runtime.Current);
        runtime.MarkFailure(owner, AgentConfigurationFailure.Unavailable);
        Assert.AreEqual(new AgentMetricSettings(45, 90, 1500, 768, 3500), runtime.Current);
        runtime.Bind(next);
        Assert.AreEqual(AgentMetricSettings.Default, runtime.Current);
        runtime.Publish(owner, saved);
        Assert.AreEqual(AgentMetricSettings.Default, runtime.Current);
        runtime.Publish(next, saved);
        Assert.AreEqual(AgentMetricSettings.Default, runtime.Current);
    }
}
