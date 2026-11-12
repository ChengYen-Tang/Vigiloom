extern alias AgentHarness;
using System.Diagnostics;
using System.Text.Json;
using Vigiloom.Agent.Core.Processes;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class ProcessInstanceReplayTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ProcessIdentity_RemainsStableAcrossIndependentAgentProcesses()
    {
        var first = await ObserveAsync(Environment.ProcessId, TestContext.CancellationToken);
        var second = await ObserveAsync(Environment.ProcessId, TestContext.CancellationToken);
        Assert.AreEqual(first.ProcessId, second.ProcessId);
        Assert.AreEqual(first.StableKey, second.StableKey);
    }

    private static async Task<ProcessObservation> ObserveAsync(int pid, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(AgentHarness::AgentHostHarnessMarker).Assembly.Location);
        start.ArgumentList.Add("--observe-process-pid");
        start.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Agent probe.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            var text = await output;
            var diagnostics = await error;
            Assert.AreEqual(0, process.ExitCode, diagnostics);
            return JsonSerializer.Deserialize<ProcessObservation>(text) ?? throw new InvalidDataException("Probe returned no observation.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }
}
