using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Agent.Hosting.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentLogStatusRuntimeTests
{
    [TestMethod]
    public void Status_PreservesObservationAgeAndClearsOnRevisionOrOwnerChange()
    {
        var clock = new FakeTimeProvider();
        var runtime = new AgentLogStatusRuntime(clock);
        var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", "state");
        var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", "local", true, 1, null);
        var other = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "other", "local", true, 1, null);
        var snapshot = AgentLogSettingsSnapshot.Create([source, other]);
        runtime.Bind(owner);
        var firstObservation = new LogSourceCycleResult(source.SourceId, LogSourceCycleStatus.Unavailable, 0,
            ObservedAt: clock.GetUtcNow(), Timestamp: clock.GetTimestamp());
        // A slow later source must not make the first source's completed observation appear new.
        clock.Advance(TimeSpan.FromSeconds(10));
        runtime.Record(owner, snapshot, false, [firstObservation], clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(42));
        runtime.Record(owner, snapshot, true, [], clock.GetUtcNow());
        var report = runtime.ForOwner(owner)!.Report;
        Assert.IsTrue(report.IsValid());
        Assert.IsTrue(report.Throttled);
        Assert.HasCount(1, report.Sources);
        Assert.AreEqual(52, report.Sources[0].AgeSeconds);
        Assert.AreEqual(AgentLogSourceState.Unavailable, report.Sources[0].State);
        var next = AgentLogSettingsSnapshot.Create([source, other], 1);
        runtime.Record(owner, next, false, [], clock.GetUtcNow());
        Assert.IsEmpty(runtime.ForOwner(owner)!.Report.Sources);
        runtime.Bind(new AgentSettingsOwner(Guid.CreateVersion7(), owner.Server, owner.StateDirectory));
        Assert.IsNull(runtime.ForOwner(owner));
    }
}
