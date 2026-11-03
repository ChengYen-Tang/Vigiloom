using Vigiloom.Modules.ServiceObservability.Domain;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class CandidateTests
{
    [TestMethod]
    public void Observe_ContinuousAndExpiredReportsUseTheCorrectWindow()
    {
        var first = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var candidate = new ServiceCandidate(Guid.CreateVersion7(), "orders", "production", "Orders", first);
        candidate.Observe("Orders API", first.AddMinutes(3));
        Assert.AreEqual(first, candidate.FirstSeen);
        Assert.AreEqual(first.AddMinutes(3), candidate.LastSeen);
        candidate.Observe("Orders API", first.AddMinutes(6).AddTicks(1));
        Assert.AreEqual(first.AddMinutes(6).AddTicks(1), candidate.FirstSeen);
        candidate.Observe("Orders API", first.AddMinutes(1));
        Assert.AreEqual(first.AddMinutes(1), candidate.FirstSeen);
        Assert.AreEqual(first.AddMinutes(1), candidate.LastSeen);
    }

    [TestMethod]
    public void Approve_DefaultsToPrivateAndPreservesTrustedIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Guid.CreateVersion7();
        var candidate = new ServiceCandidate(source, "orders", "production", "Orders", now);
        var service = new MonitoredService(candidate, now);
        Assert.AreEqual(7, service.Id.Version);
        Assert.AreEqual(source, service.SourceId);
        Assert.AreEqual("orders", service.ServiceKey);
        Assert.AreEqual("production", service.Environment);
        Assert.IsFalse(service.IsPublic);
        Assert.IsNull(service.RevokedAt);
    }
}
