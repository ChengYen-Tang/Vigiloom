using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class DebianTrackerReaderTests
{
    private const string Feed = """
        {"openssl":{"CVE-2026-34182":{"description":"example", "releases":{
          "bookworm":{"status":"resolved","fixed_version":"3.0.20-1~deb12u2","repositories":{"bookworm":"3.0.20-1~deb12u2"}},
          "bullseye":{"status":"resolved","fixed_version":"0"},
          "trixie":{"status":"open"}, "sid":{"status":"undetermined"}
        }}}}
        """;

    [TestMethod]
    public void Read_MapsReleaseScopedVendorStatus()
    {
        var records = new List<DebianTrackerRecord>();
        DebianTrackerReader.Read(Encoding.UTF8.GetBytes(Feed), 4096, 4, records.Add);
        Assert.HasCount(4, records);
        Assert.AreEqual(new DebianTrackerRecord("openssl", "CVE-2026-34182", "bookworm", DebianAdvisoryState.FixedVersion, "3.0.20-1~deb12u2"), records[0]);
        Assert.AreEqual(DebianAdvisoryState.NotAffected, records[1].State);
        Assert.AreEqual(DebianAdvisoryState.Open, records[2].State);
        Assert.AreEqual(DebianAdvisoryState.Undetermined, records[3].State);
    }

    [TestMethod]
    [DataRow("{\"pkg\":{\"CVE-1\":{\"releases\":{\"bookworm\":{\"status\":1}}}}}")]
    [DataRow("{\"pkg\":{\"CVE-1\":{\"releases\":{\"bookworm\":{\"status\":\"open\",\"fixed_version\":false}}}}}")]
    [DataRow("{\"pkg\":{\"CVE-1\":{\"releases\":{\"bookworm\":{\"status\":\"open\",\"status\":\"resolved\"}}}}}")]
    [DataRow("{\"pkg\":{\"CVE-1\":{\"releases\":{\"bookworm\":{\"status\":\"open\"}}}")]
    [DataRow("{\"pkg\":{},\"pkg\":{}}")]
    [DataRow("{}")]
    [DataRow("{\"pkg\":{\"CVE-1\":{\"description\":\"missing releases\"}}}")]
    [DataRow("{\"pkg\":{\"CVE-1\":{\"releases\":{}}}}")]
    public void Read_RejectsMalformedAndDuplicateInput(string json) =>
        Assert.ThrowsExactly<InvalidDataException>(() => DebianTrackerReader.Read(Encoding.UTF8.GetBytes(json), 4096, 100, _ => { }));

    [TestMethod]
    public void Read_RejectsConfiguredLimitsAndTrailingContent()
    {
        var bytes = Encoding.UTF8.GetBytes(Feed);
        Assert.ThrowsExactly<InvalidDataException>(() => DebianTrackerReader.Read(bytes, bytes.Length - 1, 10, _ => { }));
        Assert.ThrowsExactly<InvalidDataException>(() => DebianTrackerReader.Read(bytes, bytes.Length, 3, _ => { }));
        Assert.ThrowsExactly<InvalidDataException>(() => DebianTrackerReader.Read(Encoding.UTF8.GetBytes(Feed + " {}"), 4096, 10, _ => { }));
    }
}
