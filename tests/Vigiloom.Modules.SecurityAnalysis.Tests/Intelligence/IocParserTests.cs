using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
public sealed class IocParserTests
{
    [TestMethod]
    public void Parse_ClassifiesKnownFormatsAndKeepsUnsupportedLines()
    {
        var hashes = IocParser.Parse("iocs/hash-iocs.txt", Encoding.UTF8.GetBytes("# comment\nD41D8CD98F00B204E9800998ECF8427E;example\nnot-a-hash;source\n")).ToArray();
        Assert.HasCount(2, hashes);
        Assert.AreEqual("md5", hashes[0].Kind);
        Assert.AreEqual("d41d8cd98f00b204e9800998ecf8427e", hashes[0].Value);
        Assert.AreEqual(2, hashes[0].Number);
        Assert.AreEqual("invalid-hash", hashes[1].Reason);

        var c2 = IocParser.Parse("iocs/c2-iocs.txt", Encoding.UTF8.GetBytes("example.org\n192.0.2.1\nhttps://example.org/path\n")).ToArray();
        CollectionAssert.AreEqual(new[] { "domain", "ip", "unsupported" }, c2.Select(x => x.Kind).ToArray());
        var regex = IocParser.Parse("iocs/filename-iocs.txt", "abc;80"u8.ToArray()).Single();
        Assert.AreEqual("filename-regex-not-supported", regex.Reason);
        Assert.AreEqual("abc;80", regex.Value);
    }

    [TestMethod]
    public void Parse_PreservesUnknownFormatsAndRejectsInvalidUtf8()
    {
        var unknown = IocParser.Parse("iocs/keywords.txt", "keyword"u8.ToArray()).Single();
        Assert.AreEqual("unrecognized-ioc-format", unknown.Reason);
        Assert.Throws<InvalidDataException>(() => IocParser.Parse("iocs/c2-iocs.txt", [0xff]).ToArray());
    }

    [TestMethod]
    public void Parse_RejectsInvalidAndAmbiguousAddressForms()
    {
        var entries = IocParser.Parse("iocs/c2-iocs.txt", Encoding.UTF8.GetBytes(
            "999.999.999.999\n127.1\n0x7f000001\n2130706433\n2001:0db8::1\n")).ToArray();
        Assert.IsTrue(entries.Take(4).All(x => x.Kind == "unsupported"));
        Assert.AreEqual("ip", entries[4].Kind);
        Assert.AreEqual("2001:db8::1", entries[4].Value);
    }
}
