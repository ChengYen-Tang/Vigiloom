using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentMetricSettingsTests
{
    [TestMethod]
    public void LegacyAuditRevision_RequiresDefaultForFieldsAbsentFromOldRevision()
    {
        var settings = new AgentMetricSettings(45, 90, 1500, 768, 2500, 64);
        Span<byte> bytes = stackalloc byte[6 * sizeof(int)];
        int[] values = [1, 45, 90, 1500, 768, 2500];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32BigEndian(bytes[(i * sizeof(int))..], values[i]);
        var revision = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.IsTrue(AgentMetricSettingsSnapshot.MatchesLegacyAuditRevision(settings, revision, false, false, false));
        Assert.IsFalse(AgentMetricSettingsSnapshot.MatchesLegacyAuditRevision(settings with { IoLimitMiBPerMinute = 65 }, revision, false, false, false));
        var olderRevision = Convert.ToHexStringLower(SHA256.HashData(bytes[..20]));
        Assert.IsFalse(AgentMetricSettingsSnapshot.MatchesLegacyAuditRevision(settings, olderRevision, false, false, true));
        Assert.IsFalse(AgentMetricSettingsSnapshot.MatchesLegacyAuditRevision(settings with { ProcessCollectionBudgetMs = 2000 }, olderRevision, false, false, false));
        Assert.IsTrue(AgentMetricSettingsSnapshot.MatchesLegacyAuditRevision(settings with { ProcessCollectionBudgetMs = 2000 }, olderRevision, false, false, true));
    }

    [TestMethod]
    public void Snapshot_UsesStableIndependentRevisionAndValidatesChanges()
    {
        var settings = AgentMetricSettings.Default;
        Assert.AreEqual(new AgentMetricSettings(30, 60), settings);
        var snapshot = AgentMetricSettingsSnapshot.Create(settings);
        Assert.IsTrue(snapshot.IsValid());
        Assert.AreEqual(snapshot.Revision, AgentMetricSettingsSnapshot.Create(new(30, 60)).Revision);
        Assert.AreNotEqual(snapshot.Revision, AgentMetricSettingsSnapshot.Create(new(30, 90)).Revision);
        Assert.AreNotEqual(snapshot.Revision, AgentMetricSettingsSnapshot.Create(new(30, 60, 1500)).Revision);
        Assert.AreNotEqual(snapshot.Revision, AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 768)).Revision);
        Assert.AreNotEqual(snapshot.Revision, AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 3000)).Revision);
        Assert.AreNotEqual(snapshot.Revision, AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 2000, 8)).Revision);
        Assert.AreNotEqual(snapshot.Revision, AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 2000, 96)).Revision);
        Assert.IsFalse((snapshot with { Settings = new(30, 90) }).IsValid());
        Assert.IsFalse((snapshot with { SchemaVersion = 2 }).IsValid());
    }

    [TestMethod]
    public void Snapshot_RejectsInvalidIntervalsAndMissingJsonFields()
    {
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(9, 60)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 3601)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 99)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 60001)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 63)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 4097)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 99)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 30001)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 2000, 0)));
        Assert.Throws<ArgumentException>(() => AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 2000, 4097)));
        Assert.IsTrue(AgentMetricSettingsSnapshot.Create(new(10, 3600)).IsValid());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentMetricSettings>("{\"HostIntervalSeconds\":30}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentMetricSettings>("{\"HostIntervalSeconds\":30,\"ProcessIntervalSeconds\":60}"));
    }
}
