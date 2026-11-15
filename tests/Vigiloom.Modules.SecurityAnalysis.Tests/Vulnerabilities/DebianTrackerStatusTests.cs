using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class DebianTrackerStatusTests
{
    [TestMethod]
    [DataRow("resolved", "0", (int)DebianAdvisoryState.NotAffected, null)]
    [DataRow("resolved", "3.0.20-1~deb12u2", (int)DebianAdvisoryState.FixedVersion, "3.0.20-1~deb12u2")]
    [DataRow("open", "3.0.20-1~deb12u2", (int)DebianAdvisoryState.FixedVersion, "3.0.20-1~deb12u2")]
    [DataRow("open", null, (int)DebianAdvisoryState.Open, null)]
    [DataRow("open", "", (int)DebianAdvisoryState.Open, null)]
    [DataRow("open", " ", (int)DebianAdvisoryState.Undetermined, null)]
    [DataRow("open", "invalid", (int)DebianAdvisoryState.Undetermined, null)]
    [DataRow("open", "0", (int)DebianAdvisoryState.Undetermined, null)]
    [DataRow("resolved", null, (int)DebianAdvisoryState.Undetermined, null)]
    [DataRow("undetermined", "3.0.20-1~deb12u2", (int)DebianAdvisoryState.Undetermined, null)]
    [DataRow("resolved", "invalid", (int)DebianAdvisoryState.Undetermined, null)]
    [DataRow("unknown", "3.0.20-1~deb12u2", (int)DebianAdvisoryState.Undetermined, null)]
    [DataRow(null, null, (int)DebianAdvisoryState.Undetermined, null)]
    public void Normalize_SeparatesVendorStateFromInstalledVersion(string? status, string? fixedVersion, int expectedState, string? expectedVersion)
    {
        var result = DebianTrackerStatus.Normalize(status, fixedVersion);
        Assert.AreEqual((DebianAdvisoryState)expectedState, result.State);
        Assert.AreEqual(expectedVersion, result.FixedVersion);
    }
}
