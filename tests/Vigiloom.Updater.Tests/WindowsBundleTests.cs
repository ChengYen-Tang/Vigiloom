using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Vigiloom.Updater.Tests;

[TestClass]
public sealed class WindowsBundleTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Bundle_UsesExecutableArchitectureAndRejectsMixedPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomWindowsBundleTests", Path.GetRandomFileName());
        var agent = Path.Combine(root, "agent");
        var updater = Path.Combine(root, "updater");
        var output = Path.Combine(root, "out");
        var trust = Path.Combine(root, "root.json");
        try
        {
            Directory.CreateDirectory(agent);
            Directory.CreateDirectory(updater);
            await File.WriteAllTextAsync(trust, "test root", context.CancellationToken);
            var fingerprint = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(trust, context.CancellationToken)));
            foreach (var directory in new[] { agent, updater })
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, "coreclr.dll"), [], context.CancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(directory, "hostfxr.dll"), [], context.CancellationToken);
            }
            await File.WriteAllBytesAsync(Path.Combine(agent, "Vigiloom.Agent.dll"), [], context.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(updater, "Vigiloom.Updater.dll"), [], context.CancellationToken);

            foreach (var (architecture, machine) in new[] { ("x86", (ushort)0x014c), ("x64", (ushort)0x8664) })
            {
                await WriteExecutableAsync(Path.Combine(agent, "Vigiloom.Agent.exe"), machine, context.CancellationToken);
                await WriteExecutableAsync(Path.Combine(updater, "Vigiloom.Updater.exe"), machine, context.CancellationToken);
                var result = await BundleAsync(agent, updater, trust, fingerprint, output, context.CancellationToken);
                Assert.AreEqual(0, result.ExitCode, result.Error);
                using var archive = ZipFile.OpenRead(Path.Combine(output, $"vigiloom-agent-win-{architecture}.zip"));
                Assert.IsNotNull(archive.GetEntry("install.ps1"));
                Assert.IsNotNull(archive.GetEntry("platform.ps1"));
            }

            await WriteExecutableAsync(Path.Combine(updater, "Vigiloom.Updater.exe"), 0x014c, context.CancellationToken);
            var mixed = await BundleAsync(agent, updater, trust, fingerprint, Path.Combine(root, "mixed"), context.CancellationToken);
            Assert.AreNotEqual(0, mixed.ExitCode);
            Assert.Contains("same Windows architecture", mixed.Error);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "mixed")));

            await File.WriteAllBytesAsync(Path.Combine(agent, "Vigiloom.Agent.exe"), [1, 2, 3], context.CancellationToken);
            var invalid = await BundleAsync(agent, updater, trust, fingerprint, Path.Combine(root, "invalid"), context.CancellationToken);
            Assert.AreNotEqual(0, invalid.ExitCode);
            Assert.Contains("Invalid Windows executable", invalid.Error);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "invalid")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task WriteExecutableAsync(string path, ushort machine, CancellationToken token)
    {
        var bytes = new byte[134];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[0x3c] = 128;
        bytes[128] = (byte)'P'; bytes[129] = (byte)'E';
        bytes[132] = (byte)machine; bytes[133] = (byte)(machine >> 8);
        return File.WriteAllBytesAsync(path, bytes, token);
    }

    private static async Task<(int ExitCode, string Error)> BundleAsync(string agent, string updater, string trust, string fingerprint, string output, CancellationToken token)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WindowsPackage", "bundle.ps1");
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-AgentPublish", agent, "-UpdaterPublish", updater, "-TrustedRoot", trust, "-RootSha256", fingerprint, "-OutputDirectory", output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await stderr + await stdout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
    }
}
