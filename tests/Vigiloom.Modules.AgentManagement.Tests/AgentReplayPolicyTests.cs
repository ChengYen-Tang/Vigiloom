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
    public void ReplayWindow_ShorteningPreservesEarlierObligationsThroughDeadline()
    {
        var changed = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var sevenDays = TimeSpan.FromDays(7);
        var oneDay = TimeSpan.FromDays(1);
        var skew = TimeSpan.FromMinutes(5);
        var deadline = AgentReplayWindow.TransitionDeadline(changed, sevenDays, skew);
        var obligations = new[] { new ReplayWindowObligation(sevenDays, deadline) };
        var futureDated = changed.Add(skew);

        foreach (var now in new[] { deadline.AddTicks(-1), deadline })
        {
            var effective = AgentReplayWindow.EffectiveWindow(oneDay, now, obligations);
            Assert.AreEqual(sevenDays, effective);
            Assert.IsNull(AgentReplayWindow.Classify(futureDated, now, effective, skew, null));
        }
        var after = deadline.AddTicks(1);
        Assert.AreEqual(oneDay, AgentReplayWindow.EffectiveWindow(oneDay, after, obligations));
        Assert.AreEqual(BatchReceiptStatus.Expired,
            AgentReplayWindow.Classify(futureDated, after, oneDay, skew, null));
    }

    [TestMethod]
    public void ReplayWindow_OverlappingGenerationsAndCleanupFenceRemainIndependent()
    {
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var obligations = new[]
        {
            new ReplayWindowObligation(TimeSpan.FromDays(7), now.AddTicks(-1)),
            new ReplayWindowObligation(TimeSpan.FromDays(3), now),
            new ReplayWindowObligation(TimeSpan.FromDays(2), now.AddHours(1))
        };
        var effective = AgentReplayWindow.EffectiveWindow(TimeSpan.FromDays(1), now, obligations);
        Assert.AreEqual(TimeSpan.FromDays(3), effective);
        var fence = now.AddDays(-2);
        Assert.IsNull(AgentReplayWindow.Classify(fence, now, effective, TimeSpan.FromMinutes(5), fence));
        Assert.AreEqual(BatchReceiptStatus.Expired,
            AgentReplayWindow.Classify(fence.AddTicks(-1), now, effective, TimeSpan.FromMinutes(5), fence));
        Assert.AreEqual(TimeSpan.FromDays(2), AgentReplayWindow.EffectiveWindow(TimeSpan.FromDays(1), now.AddTicks(1), obligations));
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
