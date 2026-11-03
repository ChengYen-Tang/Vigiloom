using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
public sealed class AgentReplayPolicyTests
{
    [TestMethod]
    public void ReplayWindow_UsesInclusiveBoundaries()
    {
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.IsNull(AgentReplayWindow.Classify(now.AddHours(-24), now));
        Assert.AreEqual(BatchReceiptStatus.Expired, AgentReplayWindow.Classify(now.AddHours(-24).AddTicks(-1), now));
        Assert.IsNull(AgentReplayWindow.Classify(now.AddMinutes(5), now));
        Assert.AreEqual(BatchReceiptStatus.TimestampAhead, AgentReplayWindow.Classify(now.AddMinutes(5).AddTicks(1), now));
    }

    [TestMethod]
    public void Activate_OnlyOnceAndEnforceAfterTheOriginalGrace()
    {
        var policy = new AgentReplayPolicy();
        var start = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(policy.Activate(start));
        Assert.AreEqual(AgentReplayPhase.Grace, policy.Phase);
        Assert.AreEqual(start.AddHours(24).AddMinutes(5), policy.GraceUntil);
        Assert.IsFalse(policy.Activate(start.AddHours(1)));
        Assert.AreEqual(start, policy.ActivatedAt);
        Assert.IsFalse(policy.Enforce(policy.GraceUntil!.Value.AddTicks(-1)));
        Assert.IsTrue(policy.Enforce(policy.GraceUntil.Value));
        Assert.AreEqual(AgentReplayPhase.Enforced, policy.Phase);
        Assert.IsFalse(policy.Activate(start.AddDays(2)));
        Assert.IsFalse(policy.Enforce(start.AddDays(2)));
    }

    [TestMethod]
    public void Enforce_WithoutActivationDoesNotChangePolicy()
    {
        var policy = new AgentReplayPolicy();
        Assert.IsFalse(policy.Enforce(DateTimeOffset.UtcNow));
        Assert.AreEqual(AgentReplayPhase.Disabled, policy.Phase);
        Assert.IsNull(policy.ActivatedAt);
        Assert.IsNull(policy.GraceUntil);
        Assert.IsNull(policy.EnforcedAt);
    }
}
