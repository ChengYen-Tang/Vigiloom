using Vigiloom.Web.Api;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class AgentUpdateMetadataNameTests
{
    [TestMethod]
    [DataRow("1.root.json", true, "root", 1)]
    [DataRow("timestamp.json", true, "timestamp", 0)]
    [DataRow("2.snapshot.json", true, "snapshot", 2)]
    [DataRow("targets.json", true, "targets", 0)]
    [DataRow("catalog.json", false, "", 0)]
    [DataRow("root.json", false, "", 0)]
    [DataRow("1.timestamp.json", false, "", 0)]
    [DataRow("01.root.json", false, "", 0)]
    [DataRow("-1.root.json", false, "", 0)]
    [DataRow("2147483648.root.json", false, "", 0)]
    [DataRow("1.root.json.extra", false, "", 0)]
    public void MetadataName_OnlyAcceptsFixedTufRoles(string name, bool valid, string expectedRole, int expectedVersion)
    {
        Assert.AreEqual(valid, AgentUpdateMetadataEndpoints.TryParseName(name, out var role, out var version));
        Assert.AreEqual(expectedRole, role);
        Assert.AreEqual(expectedVersion == 0 ? null : expectedVersion, version);
    }
}
