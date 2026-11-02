using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedSettingsTests
{
    [TestMethod]
    [DataRow(39, false)]
    [DataRow(40, true)]
    [DataRow(60, true)]
    [DataRow(61, false)]
    public void Valid_EnforcesTimeoutBounds(int timeoutSeconds, bool expected)
    {
        var input = new SpeedSettingsInput(true, 6, 2048, timeoutSeconds, null);
        Assert.AreEqual(expected, SpeedSettings.Valid(input));
    }
}
