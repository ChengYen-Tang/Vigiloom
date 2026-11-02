using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class AgentSettingsTests
{
    [TestMethod]
    public void DefaultSettings_HaveStableCanonicalRevision()
    {
        var snapshot = AgentSettingsSnapshot.Create(AgentSettings.Default);
        Assert.IsTrue(snapshot.IsValid());
        Assert.AreEqual("a45c3b04b5027187289b3a640d466c78e27316457a11d055d1d7916d0d999b3c", snapshot.Revision);
        Assert.AreEqual(snapshot, JsonSerializer.Deserialize<AgentSettingsSnapshot>(JsonSerializer.Serialize(snapshot)));
    }

    [TestMethod]
    public void SettingsRevision_ChangesWithEffectiveValueAndRejectsTampering()
    {
        var original = AgentSettingsSnapshot.Create(AgentSettings.Default);
        var changed = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 60 });
        Assert.AreNotEqual(original.Revision, changed.Revision);
        Assert.IsFalse((original with { Settings = changed.Settings }).IsValid());
        Assert.IsFalse((original with { SchemaVersion = 2 }).IsValid());
        Assert.IsFalse((original with { Revision = original.Revision.ToUpperInvariant() }).IsValid());
    }

    [TestMethod]
    public void Settings_RejectValuesOutsideSupportedRanges()
    {
        var defaults = AgentSettings.Default;
        foreach (var invalid in new[]
        {
            defaults with { HeartbeatSeconds = 9 }, defaults with { SettingsPollSeconds = 3601 },
            defaults with { BatchIntervalSeconds = 0 }
        })
        {
            Assert.IsFalse(invalid.IsValid());
            Assert.Throws<ArgumentException>(() => AgentSettingsSnapshot.Create(invalid));
        }
    }

    [TestMethod]
    public void SettingsJson_RequiresEveryField()
    {
        const string incomplete = """{"HeartbeatSeconds":30,"SettingsPollSeconds":300}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentSettings>(incomplete));
    }
}
