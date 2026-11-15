using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class DebianReleaseTests
{
    [TestMethod]
    [DataRow("10", "buster")]
    [DataRow("11", "bullseye")]
    [DataRow("12.4", "bookworm")]
    [DataRow("13", "trixie")]
    [DataRow("14", null)]
    [DataRow(null, null)]
    public void TrackerRelease_UsesKnownDebianMajorVersion(string? version, string? expected) =>
        Assert.AreEqual(expected, DebianRelease.TrackerRelease(version));
}
