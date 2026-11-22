using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class UpdaterUpgradeTests
{
    [TestMethod]
    [DataRow("1.0.0", "1.1.0", "1.0.0", "1.1.0", true)]
    [DataRow("1.1.0", "1.0.0", "1.0.0", "2.0.0", false)]
    [DataRow("1.0.0", "1.0.0", "1.0.0", "2.0.0", false)]
    [DataRow("1.0.0", "1.1.0", "2.0.0", "1.0.0", false)]
    [DataRow("1.0.0", "1.1.0", "1.0.0", "1.0.0", false)]
    [DataRow(null, "1.1.0", "1.0.0", "2.0.0", false)]
    public void SelectsOnlyNewerUpdaterForAgentUpgrade(string? currentAgent, string targetAgent,
        string currentUpdater, string candidateUpdater, bool expected) =>
        Assert.AreEqual(expected, UpdaterUpgrade.ShouldUpgrade(currentAgent, targetAgent, currentUpdater, candidateUpdater));
}
