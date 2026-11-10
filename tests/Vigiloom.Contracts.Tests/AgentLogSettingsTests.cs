using Vigiloom.Contracts.Agent;
using System.Text.Json;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class AgentLogSettingsTests
{
    [TestMethod]
    public void Snapshot_IsCanonicalAndIndependentOfInputCollection()
    {
        var first = Source("first", AgentLogSourceKind.Journal, "local", null);
        var second = Source("second", AgentLogSourceKind.TextFile, "/var/log/syslog", AgentTextLogMode.SingleLine);
        var mutable = new List<AgentLogSourceSetting> { first, second };
        var snapshot = AgentLogSettingsSnapshot.Create(mutable);
        Assert.AreEqual(snapshot.Revision, AgentLogSettingsSnapshot.Create([second, first]).Revision);
        mutable.Clear();
        Assert.HasCount(2, snapshot.Sources);
        Assert.IsTrue(snapshot.IsValid());
        var roundTrip = JsonSerializer.Deserialize<AgentLogSettingsSnapshot>(JsonSerializer.Serialize(snapshot));
        Assert.IsTrue(roundTrip!.IsValid());
        Assert.IsFalse((snapshot with { Sources = [first] }).IsValid());
        Assert.AreNotEqual(snapshot.Revision, AgentLogSettingsSnapshot.Create([first, second with { Enabled = !second.Enabled }]).Revision);
    }

    [TestMethod]
    public void Transition_RequiresNewGenerationAndKeepsNativeSourceIdentity()
    {
        var file = Source("log", AgentLogSourceKind.TextFile, "/var/log/app.log", AgentTextLogMode.SingleLine);
        var previous = AgentLogSettingsSnapshot.Create([file]);
        Assert.IsFalse(AgentLogSettingsSnapshot.Create([file with { Enabled = false }]).CanFollow(previous));
        Assert.IsTrue(AgentLogSettingsSnapshot.Create([file with { Enabled = false, Generation = Guid.CreateVersion7() }]).CanFollow(previous));
        Assert.IsFalse(AgentLogSettingsSnapshot.Create([file with { Target = "/var/log/other.log", Generation = Guid.CreateVersion7() }]).CanFollow(previous));
        Assert.IsTrue(AgentLogSettingsSnapshot.Create([]).CanFollow(previous));
    }

    [TestMethod]
    public void Snapshot_RejectsDuplicatesInvalidKindsAndOversizedCollections()
    {
        var journal = Source("journal", AgentLogSourceKind.Journal, "local", null);
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create([journal, journal]));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create([journal with { Generation = Guid.NewGuid() }]));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create([journal with { Target = "remote" }]));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create([journal with { TextMode = AgentTextLogMode.SingleLine }]));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create([journal with { Name = "bad\nname" }]));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create([Source("file", AgentLogSourceKind.TextFile, "relative.log", AgentTextLogMode.SingleLine)]));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create([Source("channel", AgentLogSourceKind.WindowsEvent, new string('a', 513), null)]));
        Assert.IsTrue(AgentLogSettingsSnapshot.Create([Source("windows", AgentLogSourceKind.TextFile, @"C:\logs\a.log", AgentTextLogMode.SingleLine)]).IsValid());
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogSettingsSnapshot.Create(Enumerable.Range(0, 33).Select(_ => journal with { SourceId = Guid.CreateVersion7() })));
        Assert.IsTrue(AgentLogSettingsSnapshot.Create([]).IsValid());
    }

    private static AgentLogSourceSetting Source(string name, AgentLogSourceKind kind, string target, AgentTextLogMode? mode) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), kind, name, target, true, 1, mode);
}
