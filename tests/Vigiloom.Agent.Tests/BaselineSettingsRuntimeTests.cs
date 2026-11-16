using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Hosting.Baseline;
using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class BaselineSettingsRuntimeTests
{
    [TestMethod]
    public void UnsupportedServerStopsCollectionUntilSettingsReturn()
    {
        var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", "state");
        var snapshot = BaselineSettingsSnapshot.Create(21600, []);
        var saved = new AgentSavedBaselineSettings(snapshot, DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server);
        var runtime = new AgentBaselineSettingsRuntime();
        runtime.Bind(owner);
        runtime.Publish(owner, saved);
        Assert.AreSame(saved, runtime.ForOwner(owner));

        runtime.MarkUnsupported(owner);
        Assert.IsNull(runtime.ForOwner(owner));
        runtime.Publish(owner, saved);
        Assert.AreSame(saved, runtime.ForOwner(owner));
        runtime.Bind(new AgentSettingsOwner(Guid.CreateVersion7(), owner.Server, owner.StateDirectory));
        Assert.IsNull(runtime.ForOwner(owner));
    }
}
