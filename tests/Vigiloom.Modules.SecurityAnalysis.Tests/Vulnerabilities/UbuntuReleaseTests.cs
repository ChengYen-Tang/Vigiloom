using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class UbuntuReleaseTests
{
    [TestMethod]
    [DataRow("24.04", "Ubuntu:24.04:LTS")]
    [DataRow("22.04", "Ubuntu:22.04:LTS")]
    [DataRow("25.04", "Ubuntu:25.04")]
    [DataRow("99.04", null)]
    [DataRow(null, null)]
    public void Ecosystem_UsesKnownOrdinaryRelease(string? version, string? expected) =>
        Assert.AreEqual(expected, UbuntuRelease.Ecosystem(version));
}
