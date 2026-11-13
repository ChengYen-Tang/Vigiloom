using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Hosting.Integrity;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentIntegritySettingsRuntimeTests
{
    [TestMethod]
    public void Report_OnlyMarksWorkerAppliedAndKeepsLastSuccessAcrossFailure()
    {
        var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", "state");
        var runtime = new AgentIntegritySettingsRuntime();
        runtime.Bind(owner);
        var first = IntegritySettingsSnapshot.Create([], 0);
        var saved = new AgentSavedIntegritySettings(first, DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server);
        runtime.Publish(owner, saved);
        Assert.IsNull(runtime.Report(owner)!.Value.Report.AppliedRevision);
        var appliedAt = DateTimeOffset.UtcNow;
        Assert.AreEqual(saved, runtime.ApplyForOwner(owner, appliedAt));
        Assert.AreEqual(first.Revision, runtime.Report(owner)!.Value.Report.AppliedRevision);
        Assert.AreEqual(appliedAt, runtime.Report(owner)!.Value.Report.AppliedAt);
        var next = IntegritySettingsSnapshot.Create([], 1);
        runtime.Publish(owner, saved with { Snapshot = next, SaveToken = Guid.CreateVersion7() });
        Assert.AreEqual(first.Revision, runtime.Report(owner)!.Value.Report.AppliedRevision);
        runtime.MarkFailure(owner, AgentConfigurationFailure.Unavailable);
        Assert.AreEqual(AgentConfigurationFailure.Unavailable, runtime.Report(owner)!.Value.Report.Failure);
        Assert.AreEqual(first.Revision, runtime.Report(owner)!.Value.Report.AppliedRevision);
        Assert.AreEqual(next.Revision, runtime.ApplyForOwner(owner, appliedAt.AddSeconds(1))!.Snapshot.Revision);
        Assert.AreEqual(next.Revision, runtime.Report(owner)!.Value.Report.AppliedRevision);
        runtime.Bind(new AgentSettingsOwner(Guid.CreateVersion7(), owner.Server, owner.StateDirectory));
        Assert.IsNull(runtime.Report(owner));
    }
}
