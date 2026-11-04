using Vigiloom.Modules.ServiceObservability.Domain.Health;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthNetworkPolicyTests
{
    [TestMethod]
    public void ReplaceNetworks_NormalizesDuplicatesAndAdvancesGeneration()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new HealthNetworkPolicy(["10.10.0.0/16", "2001:db8::/32", "10.10.0.0/16"], now);
        Assert.AreEqual(1L, policy.Generation);
        Assert.AreEqual("[\"10.10.0.0/16\",\"2001:db8::/32\"]", policy.NetworksJson);
        policy.ReplaceNetworks([], now.AddSeconds(1));
        Assert.AreEqual(2L, policy.Generation);
        Assert.AreEqual("[]", policy.NetworksJson);
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("::ffff:192.168.1.0/120")]
    [DataRow("fe80::%3/64")]
    [DataRow("fe80::1%3/64")]
    [DataRow("::%3/0")]
    public void ReplaceNetworks_InvalidInput_DoesNotChangeExistingPolicy(string input)
    {
        var policy = new HealthNetworkPolicy(["10.0.0.0/8"], DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => policy.ReplaceNetworks([input], DateTimeOffset.UtcNow));
        Assert.AreEqual(1L, policy.Generation);
        Assert.AreEqual("[\"10.0.0.0/8\"]", policy.NetworksJson);
    }

    [TestMethod]
    public void ReplaceNetworks_OverCapacity_DoesNotChangeExistingPolicy()
    {
        var policy = new HealthNetworkPolicy(["10.0.0.0/8"], DateTimeOffset.UtcNow);
        var tooMany = Enumerable.Range(0, HealthNetworkPolicy.MaxNetworks + 1).Select(i => $"10.{i}.0.0/16");
        Assert.Throws<ArgumentException>(() => policy.ReplaceNetworks(tooMany, DateTimeOffset.UtcNow));
        Assert.AreEqual(1L, policy.Generation);
    }

    [TestMethod]
    public void ReplaceNetworks_SingleAddressesBecomeHostOnlyNetworks()
    {
        var policy = new HealthNetworkPolicy(["192.168.1.10", "2001:db8::1"], DateTimeOffset.UtcNow);
        var values = HealthNetworkPolicy.ParseNetworks(policy.NetworksJson);
        Assert.Contains("192.168.1.10/32", values);
        Assert.Contains("2001:db8::1/128", values);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("null")]
    [DataRow("[\"bad-cidr\"]")]
    [DataRow("[\"fe80::1%3/64\"]")]
    public void ParseNetworks_CorruptStoredPolicy_FailsClosed(string json)
    {
        Assert.Throws<InvalidOperationException>(() => HealthNetworkPolicy.ParseNetworks(json));
    }
}
