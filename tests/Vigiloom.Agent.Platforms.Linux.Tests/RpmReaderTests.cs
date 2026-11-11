using System.Diagnostics;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Linux.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class RpmReaderTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void Parse_PreservesEpochArchitectureAndMultipleVersions()
    {
        var (status, first) = RpmReader.Parse("kernel\t0:6.14.0-1.fc42\tx86_64\tFedora Project");
        var (_, second) = RpmReader.Parse("kernel\t0:6.15.0-1.fc42\tx86_64\tFedora Project");
        Assert.AreEqual(PackageCommand.LineStatus.Installed, status);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual("0:6.14.0-1.fc42", first.Version);
        Assert.AreEqual("x86_64", first.Architecture);
        Assert.AreNotEqual(first.SourceKey, second.SourceKey);
        Assert.AreEqual(PackageCommand.LineStatus.Invalid, RpmReader.Parse("kernel\tmissing-fields").Status);
        Assert.AreEqual(PackageCommand.LineStatus.Invalid, RpmReader.Parse($"kernel\t{new string('x', 2048)}\tx86_64\tFedora").Status);
    }

    [TestMethod]
    public async Task ReadAsync_OnLinuxReportsInstalledPackagesOrUnsupported()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var result = await RpmReader.ReadAsync(TestContext.CancellationToken);
        if (!File.Exists("/usr/bin/rpm")) Assert.AreEqual(InventoryReadStatus.Unsupported, result.Status);
        else
        {
            Assert.AreEqual(InventoryReadStatus.Complete, result.Status);
            var start = new ProcessStartInfo("/usr/bin/rpm")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            start.ArgumentList.Add("-qa");
            start.ArgumentList.Add("--qf");
            start.ArgumentList.Add("x\\n");
            using var rpm = Process.Start(start) ?? throw new InvalidOperationException("Could not start the RPM query.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            Exception? failure = null;
            try
            {
                var count = 0;
                while (await rpm.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    Assert.AreEqual("x", line);
                    Assert.IsLessThanOrEqualTo(20000, ++count);
                }
                await rpm.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, rpm.ExitCode);
                Assert.HasCount(count, result.Items);
            }
            catch (Exception error) { failure = error; }
            var cleanupErrors = new List<Exception>();
            try
            {
                if (!rpm.HasExited) rpm.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (rpm.HasExited) { }
            catch (Exception error) { cleanupErrors.Add(error); }
            try { await rpm.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch (Exception error) { cleanupErrors.Add(error); }
            if (failure is not null) cleanupErrors.Insert(0, failure);
            if (cleanupErrors.Count > 0) throw new AggregateException(cleanupErrors);
        }
    }
}
