using Vigiloom.Modules.NetworkMonitoring.Domain;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class PingSampleTests
{
    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(-1d)]
    public void SuccessfulSample_RejectsInvalidLatency(double latency)
    {
        Assert.Throws<ArgumentException>(() => new PingSample(Guid.CreateVersion7(), "central", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, latency));
    }
}
