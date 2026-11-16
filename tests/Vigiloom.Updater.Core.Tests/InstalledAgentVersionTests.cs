using System.Reflection;
using Vigiloom.Agent.Hosting;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class InstalledAgentVersionTests(TestContext context)
{
    [TestMethod]
    [DataRow("1.2.3-beta.1+commit", "1.2.3-beta.1")]
    [DataRow("1.2.3+commit", "1.2.3")]
    [DataRow("1.2", null)]
    [DataRow("not-a-version", null)]
    [DataRow(null, null)]
    public void NormalizesPublishedInformationalVersion(string? value, string? expected) =>
        Assert.AreEqual(expected, InstalledAgentVersion.Normalize(value));

    [TestMethod]
    public void ReadsRealAgentAssemblyVersionAndRejectsOtherAssemblies()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"agent-version-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var agent = typeof(AgentHost).Assembly;
            var informationalVersion = agent.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
            var path = Path.Combine(directory, "Vigiloom.Agent.dll");
            File.Copy(agent.Location, path);
            Assert.AreEqual(informationalVersion, InstalledAgentVersion.Read(directory));
            File.Copy(typeof(InstalledAgentVersion).Assembly.Location, path, overwrite: true);
            Assert.ThrowsExactly<InvalidDataException>(() => InstalledAgentVersion.Read(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
