using System.Runtime.Versioning;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentRuntimeTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("\"C:\\Program Files\\Vigiloom\\Vigiloom.Agent.exe\" --server \"https://example.test\"", " --server \"https://example.test\"")]
    [DataRow("C:\\Vigiloom.Agent.exe\t--state C:\\state", "\t--state C:\\state")]
    [DataRow("\"C:\\Vigiloom.Agent.exe\"", "")]
    [DataRow("C:\\Vigiloom.Agent.exe", "")]
    public void PreservesServiceArguments(string command, string expected)
        => Assert.AreEqual(expected, WindowsAgentRuntime.ParseServiceCommand(command).Arguments);

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void ReadsQuotedServiceExecutableWithoutArguments()
    {
        var parsed = WindowsAgentRuntime.ParseServiceCommand("\"C:\\Program Files\\Vigiloom\\Vigiloom.Agent.exe\" --state C:\\state");
        Assert.AreEqual("C:\\Program Files\\Vigiloom\\Vigiloom.Agent.exe", parsed.Executable);
        Assert.AreEqual(" --state C:\\state", parsed.Arguments);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("")]
    [DataRow("\"C:\\Agent.exe")]
    [DataRow("C:\\Program Files\\Vigiloom\\Vigiloom.Agent.exe --state C:\\state")]
    [DataRow("\"C:\\Vigiloom.Agent.exe\"unexpected")]
    public void RejectsMalformedServiceCommand(string command)
        => Assert.ThrowsExactly<InvalidOperationException>(() => WindowsAgentRuntime.ParseServiceCommand(command));

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task RejectsMissingOrOutsideVersionBeforeAccessingService()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"service-{Guid.NewGuid():N}");
        var root = Path.Combine(directory, "install");
        var outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "Vigiloom.Agent.exe"), "outside", context.CancellationToken);
            var runtime = new WindowsAgentRuntime(root);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SelectVersionAsync(outside, context.CancellationToken));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SelectVersionAsync(Path.Combine(root, "missing"), context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
