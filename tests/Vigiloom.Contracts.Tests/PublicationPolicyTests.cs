using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class PublicationPolicyTests
{
    [TestMethod]
    public void PublicName_RejectsExcessiveAddressLikeTextWithoutAcceptingAnAddressAfterIt()
    {
        Assert.IsFalse(PublicationPolicy.TryValidateName(string.Concat(Enumerable.Repeat("a:", 100)) + "192.168.1.1", out _));
        Assert.IsFalse(PublicationPolicy.TryValidateName(string.Concat(Enumerable.Repeat("x.", 128)), out _));
    }

    [TestMethod]
    [DataRow("台北對外網路")]
    [DataRow("Router 1")]
    [DataRow("Orders service")]
    public void PublicName_AllowsNamesWithoutAddresses(string value) =>
        Assert.IsTrue(PublicationPolicy.TryValidateName(value, out _));

    [TestMethod]
    [DataRow("192.168.1.20")]
    [DataRow("router-192.168.1.20")]
    [DataRow("router192.168.1.20east")]
    [DataRow("fe80::1")]
    [DataRow("router-fe80::1")]
    [DataRow("::1")]
    [DataRow("http://127.1")]
    [DataRow("router-0x7f.0.0.1")]
    [DataRow("router-2130706433")]
    [DataRow("router-2130706433east")]
    [DataRow("router-0x7f000001box")]
    public void PublicName_RejectsEmbeddedAddresses(string value) =>
        Assert.IsFalse(PublicationPolicy.TryValidateName(value, out _));
}
