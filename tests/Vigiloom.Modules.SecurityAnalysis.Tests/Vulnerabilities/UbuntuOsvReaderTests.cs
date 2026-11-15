using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class UbuntuOsvReaderTests
{
    private const string Record = """
        {"id":"UBUNTU-CVE-2025-6491","modified":"2025-07-18T17:02:45Z",
         "affected":[{"package":{"ecosystem":"Ubuntu:24.04:LTS","name":"php8.3"},
          "ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"8.3.6-0ubuntu0.24.04.2"}]}],
          "versions":["8.3.6-0ubuntu0.24.04.1"]}]}
        """;

    [TestMethod]
    public void Read_MapsSourcePackageRangesAndDoesNotAssumeFreshness()
    {
        var record = UbuntuOsvReader.Read(Encoding.UTF8.GetBytes(Record), 4096);
        Assert.AreEqual("UBUNTU-CVE-2025-6491", record.Id);
        Assert.IsFalse(record.Withdrawn);
        Assert.HasCount(1, record.Packages);
        var package = record.Packages[0];
        Assert.IsFalse(package.CompleteAndCurrent);
        var inventory = new UbuntuOsvEvidence("Ubuntu:24.04:LTS", "php8.3", "8.3.6-0ubuntu0.24.04.1", PackageOrigin.AssumedDistribution, true);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(inventory, package).State);
        Assert.AreEqual(MatchState.Affected, UbuntuOsvMatch.Evaluate(inventory, package with { CompleteAndCurrent = true }).State);
    }

    [TestMethod]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"modified\":\"2025-01-01T00:00:00\"}")]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"modified\":\"2025-01-01T00:00:00Z\",\"id\":\"UBUNTU-CVE-2\"}")]
    [DataRow("{\"id\":\"OTHER-1\",\"modified\":\"2025-01-01T00:00:00Z\"}")]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"modified\":\"2025-01-01T00:00:00Z\",\"affected\":{}}")]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"schema_version\":\"2.0.0\",\"modified\":\"2025-01-01T00:00:00Z\"}")]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"schema_version\":\"1.01.0\",\"modified\":\"2025-01-01T00:00:00Z\"}")]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"schema_version\":\"1.0.0\\n\",\"modified\":\"2025-01-01T00:00:00Z\"}")]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"modified\":\"2025-01-01T00:00:00Z\",\"withdrawn\":null}")]
    [DataRow("{\"id\":\"UBUNTU-CVE-1\",\"modified\":\"2025-01-01T00:00:00Z\",\"withdrawn\":true}")]
    public void Read_RejectsMalformedRecords(string json) =>
        Assert.ThrowsExactly<InvalidDataException>(() => UbuntuOsvReader.Read(Encoding.UTF8.GetBytes(json), 4096));

    [TestMethod]
    public void Read_AcceptsVersionedAndWithdrawnRecord()
    {
        var json = """{"id":"UBUNTU-CVE-1","schema_version":"1.7.0","modified":"2025-01-01T00:00:00Z","withdrawn":"2025-01-02T00:00:00Z"}""";
        var record = UbuntuOsvReader.Read(Encoding.UTF8.GetBytes(json), 4096);
        Assert.IsTrue(record.Withdrawn);
        Assert.IsEmpty(record.Packages);
    }

    [TestMethod]
    public void Read_UnknownEventKindNeverBecomesFixed()
    {
        var record = UbuntuOsvReader.Read(Encoding.UTF8.GetBytes(Record.Replace("\"fixed\"", "\"future_kind\"", StringComparison.Ordinal)), 4096);
        var inventory = new UbuntuOsvEvidence("Ubuntu:24.04:LTS", "php8.3", "8.3.6-0ubuntu0.24.04.2", PackageOrigin.AssumedDistribution, true);
        Assert.AreEqual(MatchState.Unknown, UbuntuOsvMatch.Evaluate(inventory, record.Packages[0] with { CompleteAndCurrent = true }).State);
    }

    [TestMethod]
    [DataRow("[{\"type\":\"Ubuntu\",\"score\":\"high\"}]", "high")]
    [DataRow("[{\"type\":\"CVSS_V3\",\"score\":\"CVSS:3.1/AV:N\"}]", null)]
    [DataRow("[{\"type\":\"Ubuntu\",\"score\":\"high\"},{\"type\":\"Ubuntu\",\"score\":\"low\"}]", null)]
    [DataRow("[{\"type\":\"Ubuntu\",\"score\":\"future-value\"}]", null)]
    [DataRow("null", null)]
    public void Read_UsesOnlyUnambiguousKnownUbuntuPriority(string severity, string? expected)
    {
        var json = "{\"id\":\"UBUNTU-CVE-1\",\"modified\":\"2025-01-01T00:00:00Z\",\"severity\":" + severity + "}";
        var record = UbuntuOsvReader.Read(Encoding.UTF8.GetBytes(json), 4096);
        Assert.AreEqual(expected, record.UbuntuPriority);
    }
}
