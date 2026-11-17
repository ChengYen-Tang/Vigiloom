using System.Diagnostics;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class RuntimeCheckTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CheckRuntime_ExitsWithoutStartingAgentHost()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Vigiloom.Agent.dll"));
        start.ArgumentList.Add("check-runtime");
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Agent.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(testContext.CancellationToken);
            var errors = process.StandardError.ReadToEndAsync(testContext.CancellationToken);
            await process.WaitForExitAsync(testContext.CancellationToken);
            Assert.AreEqual(0, process.ExitCode, await errors);
            Assert.Contains("Agent runtime check passed.", await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupTimeout.Token);
        }
    }
}
