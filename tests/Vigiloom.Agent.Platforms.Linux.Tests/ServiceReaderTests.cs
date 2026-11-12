using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Linux.Processes;
using System.Diagnostics;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class ServiceReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReadAsync_DistinguishesUnsupportedSystemdAndObservedServices()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var result = await ServiceReader.ReadAsync(TestContext.CancellationToken);
        if (!Directory.Exists("/run/systemd/system")) { Assert.AreEqual(InventoryReadStatus.Unsupported, result.Status); return; }
        if (result.Status == InventoryReadStatus.PermissionDenied) { Assert.Inconclusive("Systemd access denied."); return; }
        Assert.IsTrue(result.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
        Assert.IsNotEmpty(result.Items);
        Assert.IsTrue(result.Items.All(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Status)));
        Assert.AreEqual(result.Items.Count, result.Items.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count());
        var start = new ProcessStartInfo("/usr/bin/systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("is-active");
        start.ArgumentList.Add("systemd-journald.service");
        using var check = Process.Start(start) ?? throw new InvalidOperationException("Could not check systemd.");
        await check.WaitForExitAsync(TestContext.CancellationToken);
        if (check.ExitCode == 0)
            Assert.IsTrue(result.Items.Any(x => x.Key == "systemd-journald.service" && x.Status == "active"));
    }
}
