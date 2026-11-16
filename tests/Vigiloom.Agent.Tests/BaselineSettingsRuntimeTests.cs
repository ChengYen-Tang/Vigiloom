using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Hosting.Baseline;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class BaselineSettingsRuntimeTests
{
    [TestMethod]
    public void ReportsAppliedPolicyAndSyncFailureForCurrentOwner()
    {
        var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", "state");
        var now = DateTimeOffset.UtcNow;
        var snapshot = BaselineSettingsSnapshot.Create(21600, []);
        var saved = new AgentSavedBaselineSettings(snapshot, now, Guid.CreateVersion7(), owner.AgentId, owner.Server);
        var runtime = new AgentBaselineSettingsRuntime();
        runtime.Bind(owner);
        runtime.Publish(owner, saved, now);
        Assert.IsNull(runtime.Report(owner)!.Value.Report.AppliedRevision);
        Assert.AreSame(saved, runtime.ApplyForOwner(owner, now.AddSeconds(1)));
        runtime.MarkFailure(owner, AgentConfigurationFailure.Unavailable);
        var report = runtime.Report(owner)!.Value.Report;
        Assert.AreEqual(snapshot.Revision, report.AppliedRevision);
        Assert.AreEqual(now.AddSeconds(1), report.AppliedAt);
        Assert.AreEqual(now, report.LastSyncedAt);
        Assert.AreEqual(AgentConfigurationFailure.Unavailable, report.Failure);
        Assert.IsTrue(report.IsValid());
        runtime.Bind(new AgentSettingsOwner(Guid.CreateVersion7(), owner.Server, owner.StateDirectory));
        Assert.IsNull(runtime.Report(owner));
    }

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
