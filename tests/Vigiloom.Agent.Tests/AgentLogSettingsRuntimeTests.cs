using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Hosting.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentLogSettingsRuntimeTests
{
    [TestMethod]
    public void Runtime_FencesOwnerAndKeepsUnauthorizedBlockedUntilValidSync()
    {
        var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://one.test/", "state");
        var next = new AgentSettingsOwner(Guid.CreateVersion7(), "https://two.test/", "state");
        var runtime = new AgentLogSettingsRuntime();
        var snapshot = AgentLogSettingsSnapshot.Create([]);
        var saved = new AgentSavedLogSettings(snapshot, DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server);
        runtime.Bind(owner);
        runtime.Publish(owner, saved);
        Assert.AreEqual(snapshot, runtime.ForOwner(owner));
        runtime.MarkFailure(owner, AgentConfigurationFailure.Unavailable);
        Assert.AreEqual(snapshot, runtime.ForOwner(owner));
        runtime.MarkFailure(owner, AgentConfigurationFailure.Unauthorized);
        Assert.IsNull(runtime.ForOwner(owner));
        runtime.MarkFailure(owner, AgentConfigurationFailure.Unavailable);
        Assert.IsNull(runtime.ForOwner(owner));
        runtime.Publish(owner, saved);
        Assert.AreEqual(snapshot, runtime.ForOwner(owner));
        runtime.Bind(next);
        runtime.Publish(owner, saved);
        runtime.Publish(next, saved);
        Assert.IsNull(runtime.ForOwner(owner));
        Assert.IsNull(runtime.ForOwner(next));
    }
}
