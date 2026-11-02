using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class IdentityProcessTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(45000, CooperativeCancellation = true)]
    public async Task PendingIdentity_IsLockedAcrossProcessesAndSurvivesAgentTermination()
    {
        var token = testContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "VigiloomIdentityProcessTests", Path.GetRandomFileName());
        var agentAssembly = Path.Combine(AppContext.BaseDirectory, "Vigiloom.Agent.dll");
        var enrollment = $"vle_{Guid.CreateVersion7():D}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // Start the Agent itself, rather than a dotnet-run parent whose child can outlive WaitForExitAsync.
        foreach (var argument in new[] { agentAssembly, "enroll" }) start.ArgumentList.Add(argument);
        start.Environment["Vigiloom__Agent__Server"] = $"https://127.0.0.1:{port}/";
        start.Environment["Vigiloom__Agent__StateDirectory"] = root;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start test Agent.");
        Exception? primaryFailure = null;
        try
        {
            await process.StandardInput.WriteLineAsync(enrollment.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            using var accepted = await listener.AcceptTcpClientAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(25), token);
            var vault = new IdentityVault(root);
            IdentityVault.Session? contender = null;
            try { Assert.Throws<IOException>(() => contender = vault.Acquire()); }
            finally { if (contender is not null) await contender.DisposeAsync(); }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token);
            await using var session = vault.Acquire();
            var state = await session.ReadAsync(token);
            Assert.IsNotNull(state); Assert.IsNull(state.AgentId); Assert.AreEqual(enrollment, state.EnrollmentSecret);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomIdentityProcessTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                throw new AggregateException("The test and its cleanup both failed.", primaryFailure, cleanupError);
            }
        }
    }
}
