using Vigiloom.Modules.Resources.Domain;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
public sealed class TargetAddressTests
{
    [TestMethod]
    [DataRow("  EXAMPLE.COM. ", "example.com")]
    [DataRow("2001:0DB8:0:0::1", "2001:db8::1")]
    [DataRow("192.0.2.1", "192.0.2.1")]
    [DataRow("bücher.example", "xn--bcher-kva.example")]
    public void Normalize_AcceptsIpAndDns(string input, string expected)
    {
        Assert.IsTrue(TargetAddress.TryNormalize(input, out var result));
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    [DataRow("https://example.com")]
    [DataRow("example.com:80")]
    [DataRow("bad..example")]
    [DataRow("999.1.1.1")]
    [DataRow("01.2.3.4")]
    [DataRow("fe80::1%4")]
    [DataRow("")]
    public void Normalize_RejectsInvalidTargets(string input) => Assert.IsFalse(TargetAddress.TryNormalize(input, out _));
}
