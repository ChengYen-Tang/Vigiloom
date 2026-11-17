using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Vigiloom.UpdateSecurity;
using Vigiloom.Updater.Hosting;

namespace Vigiloom.Updater.Tests;

[TestClass]
public sealed class UpdaterProvisioningTests(TestContext context)
{
    [TestMethod]
    public async Task TrustedRootRequiresMatchingFingerprintAndCannotBeReplaced()
    {
        var state = Path.Combine(context.TestRunDirectory!, $"provision-{Guid.NewGuid():N}");
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "root.json");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(root, context.CancellationToken)));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Vigiloom:Updater:StateDirectory"] = state }).Build();
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdaterProvisioning.ProvisionTrustAsync(config, root,
                new string('0', 64), context.CancellationToken));
            Assert.IsFalse(Directory.Exists(Path.Combine(state, "trust")));
            await UpdaterProvisioning.ProvisionTrustAsync(config, root, fingerprint, context.CancellationToken);
            await using (var lease = await new LocalTrustStore(Path.Combine(state, "trust")).AcquireAsync(context.CancellationToken))
                Assert.IsNotNull(lease.LoadMetadata("root"));
            await Assert.ThrowsExactlyAsync<IOException>(() => UpdaterProvisioning.ProvisionTrustAsync(config, root, fingerprint, context.CancellationToken));
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, recursive: true); }
    }

    [TestMethod]
    public async Task MissingOrRelativeStateDirectoryIsRejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Vigiloom:Updater:StateDirectory"] = "state" }).Build();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => UpdaterProvisioning.ProvisionTrustAsync(config,
            Path.GetFullPath("root.json"), new string('0', 64), context.CancellationToken));
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task InstallerCommandProvisionsStoreWithoutStartingService()
    {
        var state = Path.Combine(context.TestRunDirectory!, $"provision-command-{Guid.NewGuid():N}");
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "root.json");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(root, context.CancellationToken)));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { typeof(UpdaterHost).Assembly.Location, "provision-trust", root, fingerprint }
        };
        start.Environment["Vigiloom__Updater__StateDirectory"] = state;
        var childStopped = true;
        try
        {
            using var process = Process.Start(start)!;
            childStopped = false;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
                var error = process.StandardError.ReadToEndAsync(context.CancellationToken);
                await process.WaitForExitAsync(context.CancellationToken);
                Assert.AreEqual(0, process.ExitCode, (await output) + (await error));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupTimeout.Token);
                childStopped = true;
            }
            await using var lease = await new LocalTrustStore(Path.Combine(state, "trust")).AcquireAsync(context.CancellationToken);
            Assert.IsNotNull(lease.LoadMetadata("root"));
        }
        finally { if (childStopped && Directory.Exists(state)) Directory.Delete(state, recursive: true); }
    }
}
