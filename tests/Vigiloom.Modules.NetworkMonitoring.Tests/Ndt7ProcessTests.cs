using System.Diagnostics;
using System.Text.Json;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;
[TestClass]
public sealed class Ndt7ProcessTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    [TestCategory("Integration")]
    public async Task BundledClientRejectsInvalidInputBeforeNetworkAccess()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", OperatingSystem.IsWindows() ? "vigiloom-ndt7.exe" : "vigiloom-ndt7");
        Assert.IsTrue(File.Exists(path), "Build the bundled NDT7 client with build/build-ndt7.ps1 before testing.");
        using var process = new Process { StartInfo = new(path) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        Assert.IsTrue(process.Start());
        await process.StandardInput.WriteLineAsync("{\"MaxBytes\":0,\"TimeoutSeconds\":0}");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode);
            using var response = JsonDocument.Parse(await process.StandardOutput.ReadToEndAsync(timeout.Token));
            Assert.AreEqual("InvalidInput", response.RootElement.GetProperty("Status").GetString());
            Assert.AreEqual(0L, response.RootElement.GetProperty("Bytes").GetInt64());
            Assert.IsEmpty(await process.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    [TestMethod]
    public async Task CancelledProbeDoesNotStartClientOrUseNetwork()
    {
        using var stop = new CancellationTokenSource(); await stop.CancelAsync();
        var result = await new Ndt7ProcessProbe().MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, 5), stop.Token);
        Assert.AreEqual(SpeedTestState.Cancelled, result.State); Assert.IsNull(result.Bytes); Assert.IsNull(result.DownloadMbps);
    }
}
