using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Hosting.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class IntegrityScheduleTests
{
    [TestMethod]
    public void Next_RoundRobinsDueScopesAndResetsChangedPolicies()
    {
        var clock = new FakeTimeProvider();
        var schedule = new IntegritySchedule(clock);
        var first = Scope();
        var second = Scope();
        IntegrityScope[] scopes = [first, second];
        Assert.AreEqual(first, schedule.Next(scopes));
        schedule.Finish(first);
        Assert.AreEqual(second, schedule.Next(scopes));
        schedule.Finish(second);
        Assert.IsNull(schedule.Next(scopes));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(first, schedule.Next(scopes));
        schedule.Finish(first);
        var changed = second with { Revision = 2 };
        Assert.AreEqual(changed, schedule.Next([first, changed]));
        schedule.Finish(changed);
        Assert.IsNull(schedule.Next([first, changed]));
        Assert.IsNull(schedule.Next([first, changed with { Enabled = false }]));
        Assert.AreEqual(changed, schedule.Next([first, changed]));
    }

    private static IntegrityScope Scope() => new(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, "C:\\temp",
        true, ImmutableArray<string>.Empty, 60, 100, 1024, 4096, 4);
}
