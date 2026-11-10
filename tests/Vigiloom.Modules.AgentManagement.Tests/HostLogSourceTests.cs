using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
public sealed class HostLogSourceTests
{
    [TestMethod]
    public void Change_CreatesNewGenerationAndRetirementPreventsIdentityReuse()
    {
        var now = DateTimeOffset.UtcNow;
        var actor = Guid.CreateVersion7();
        var source = new HostLogSource(Guid.CreateVersion7(), AgentLogSourceKind.TextFile, "app", "/var/log/app.log", true,
            AgentTextLogMode.SingleLine, actor, now);
        var previous = source.ToSetting();
        Assert.IsFalse(source.Update("app", true, AgentTextLogMode.SingleLine, actor, now));
        Assert.AreEqual(previous.Generation, source.Generation);
        Assert.ThrowsExactly<ArgumentException>(() => source.Update("app", true, null, actor, now));
        Assert.AreEqual(previous, source.ToSetting());
        Assert.IsTrue(source.Update("app", false, AgentTextLogMode.IndentedContinuation, actor, now));
        Assert.AreNotEqual(previous.Generation, source.Generation);
        Assert.AreEqual(previous.SourceId, source.Id);
        Assert.AreEqual(previous.Target, source.Target);
        Assert.IsTrue(AgentLogSettingsSnapshot.Create([source.ToSetting()]).CanFollow(AgentLogSettingsSnapshot.Create([previous])));
        source.Retire(actor, now);
        Assert.IsNotNull(source.RetiredAt);
        Assert.IsFalse(source.Enabled);
        Assert.ThrowsExactly<InvalidOperationException>(() => source.Update("app", true, AgentTextLogMode.SingleLine, actor, now));
    }
}
