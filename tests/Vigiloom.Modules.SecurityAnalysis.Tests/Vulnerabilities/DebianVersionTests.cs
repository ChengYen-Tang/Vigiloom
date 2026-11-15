using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using System.Diagnostics;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class DebianVersionTests
{
    [TestMethod]
    [DataRow("1:1.0-1", "2.0-1", 1)]
    [DataRow("1.0~rc1-1", "1.0-1", -1)]
    [DataRow("1.0-1", "1.0-1+deb12u1", -1)]
    [DataRow("1.0-1+deb12u1", "1.0-1+deb12u1", 0)]
    [DataRow("1.0-1+deb12u2", "1.0-1+deb12u1", 1)]
    [DataRow("1.0", "1.0-0", 0)]
    [DataRow("1.001", "1.1", 0)]
    [DataRow("1.9999999999999999999999", "1.10000000000000000000000", -1)]
    [DataRow("1.0~~", "1.0~", -1)]
    [DataRow("1.0a", "1.0+", -1)]
    [DataRow("1.0A", "1.0a", -1)]
    [DataRow("1.0", "1.0a", -1)]
    [DataRow("1.0a", "1.0.", -1)]
    public void Compare_UsesDebianPolicy(string left, string right, int expected)
    {
        Assert.IsTrue(DebianVersion.TryCompare(left, right, out var result));
        Assert.AreEqual(expected, Math.Sign(result));
        Assert.IsTrue(DebianVersion.TryCompare(right, left, out var reverse));
        Assert.AreEqual(-expected, Math.Sign(reverse));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("a1")]
    [DataRow(":1.0")]
    [DataRow("1.0-")]
    [DataRow("1.0-1:2")]
    [DataRow("1:2:3")]
    [DataRow("1.0 2")]
    public void Compare_RejectsMalformedVersions(string value)
    {
        Assert.IsFalse(DebianVersion.TryCompare(value, "1.0", out _));
        Assert.IsFalse(DebianVersion.TryCompare("1.0", value, out _));
    }

    [TestMethod]
    public void Compare_RejectsNullAndExcessiveLength()
    {
        Assert.IsFalse(DebianVersion.TryCompare(null, "1.0", out _));
        Assert.IsFalse(DebianVersion.TryCompare("1.0", null, out _));
        Assert.IsTrue(DebianVersion.TryCompare("1" + new string('0', 2047), "1.0", out _));
        Assert.IsFalse(DebianVersion.TryCompare("1" + new string('0', 2048), "1.0", out _));
    }

    [TestMethod]
    public async Task Compare_MatchesDpkgWhenAvailable()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/dpkg"))
        {
            Assert.Inconclusive("Requires dpkg on Linux.");
            return;
        }
        (string Left, string Right)[] pairs =
        [
            ("1:1.0-1", "2.0-1"), ("1.0~rc1-1", "1.0-1"),
            ("1.0-1", "1.0-1+deb12u1"), ("1.0", "1.0-0"),
            ("1.001", "1.1"), ("1.0~~", "1.0~"),
            ("1.0a", "1.0+"), ("1.0A", "1.0a"),
            ("1.0", "1.0a"), ("1.0a", "1.0.")
        ];
        foreach (var (left, right) in pairs)
        {
            Assert.IsTrue(DebianVersion.TryCompare(left, right, out var actual));
            var operation = actual < 0 ? "lt" : actual > 0 ? "gt" : "eq";
            var start = new ProcessStartInfo("/usr/bin/dpkg") { UseShellExecute = false, RedirectStandardError = false };
            start.ArgumentList.Add("--compare-versions"); start.ArgumentList.Add(left); start.ArgumentList.Add(operation); start.ArgumentList.Add(right);
            using var process = Process.Start(start);
            Assert.IsNotNull(process);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Exception? failure = null;
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, $"dpkg disagreed for {left} {operation} {right}.");
            }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                Exception? cleanupError = null;
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        catch (Exception ex) { cleanupError = ex; }
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await process.WaitForExitAsync(cleanup.Token); }
                        catch (Exception ex) { cleanupError = cleanupError is null ? ex : new AggregateException(cleanupError, ex); }
                    }
                    if (cleanupError is not null) throw cleanupError;
                }
                catch (Exception ex) when (failure is not null) { failure.Data["DpkgCleanupError"] = ex; }
            }
        }
    }
}
