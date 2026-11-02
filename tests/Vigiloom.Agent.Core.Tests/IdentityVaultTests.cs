using System.Text;
using System.Net;
using System.Security.Cryptography;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class IdentityVaultTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task IdentityState_IsProtectedAndSurvivesReopening()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomIdentityTests", Path.GetRandomFileName());
        var vault = new IdentityVault(root);
        const string secret = "test-only-machine-secret-never-a-real-credential";
        try
        {
            await using (var session = vault.Acquire())
            {
                Assert.IsNull(await session.ReadAsync(CancellationToken.None));
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", MachineSecret = secret }, CancellationToken.None);
                Assert.Throws<IOException>(() => vault.Acquire());
                if (OperatingSystem.IsWindows())
                {
                    var moved = root + "-moved";
                    try { Assert.Throws<IOException>(() => Directory.Move(root, moved)); }
                    finally { if (Directory.Exists(moved)) Directory.Move(moved, root); }
                }
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => session.WriteAsync(new IdentityState { Server = "https://example.test/", MachineSecret = "replacement" }, cancelled.Token));
            }
            await using (var session = vault.Acquire())
            {
                var state = await session.ReadAsync(CancellationToken.None);
                Assert.IsNotNull(state); Assert.AreEqual(secret, state.MachineSecret);
            }
            if (OperatingSystem.IsWindows())
                Assert.DoesNotContain(secret, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(root, "identity.dat"), TestContext.CancellationToken)));
            else if (OperatingSystem.IsLinux())
            {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "identity.dat")));
                var writable = Path.Combine(root, "writable");
                Directory.CreateDirectory(writable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(writable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                Assert.Throws<UnauthorizedAccessException>(() => new IdentityVault(Path.Combine(writable, "state")).Acquire());
                var target = Path.Combine(root, "real-directory"); Directory.CreateDirectory(target);
                var link = Path.Combine(root, "directory-link"); Directory.CreateSymbolicLink(link, target);
                try { Assert.Throws<UnauthorizedAccessException>(() => new IdentityVault(Path.Combine(link, "state")).Acquire()); }
                finally { Directory.Delete(link); }
                if (Environment.UserName == "root")
                {
                    var untrusted = Path.Combine(root, "foreign");
                    Directory.CreateDirectory(untrusted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    await ChangeOwnerAsync(untrusted);
                    Assert.Throws<UnauthorizedAccessException>(() => new IdentityVault(untrusted).Acquire());
                    var identityPath = Path.Combine(root, "identity.dat");
                    await ChangeOwnerAsync(identityPath);
                    await using var session = vault.Acquire();
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.ReadAsync(TestContext.CancellationToken));
                }
            }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomIdentityTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RelativeOrRootIdentityDirectory_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new IdentityVault("relative"));
        Assert.Throws<ArgumentException>(() => new IdentityVault(Path.GetPathRoot(Path.GetTempPath())!));
    }

    [TestMethod]
    public async Task ExpiredPendingEnrollment_CanOnlyBeReplacedExplicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomIdentityTests", Path.GetRandomFileName());
        var vault = new IdentityVault(root);
        var server = new Uri("https://example.test/");
        var first = EnrollmentSecret(); var next = EnrollmentSecret();
        using var http = new HttpClient(new RejectedHandler());
        var client = new IdentityClient(http);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnrollAsync(vault, server, first, TestContext.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnrollAsync(vault, server, next, TestContext.CancellationToken));
            await client.ResetPendingAsync(vault, server, next, TestContext.CancellationToken);
            await using (var session = vault.Acquire())
            {
                var state = await session.ReadAsync(TestContext.CancellationToken);
                Assert.IsNotNull(state); Assert.AreEqual(next, state.EnrollmentSecret); Assert.IsNull(state.AgentId);
                state.AgentId = Guid.CreateVersion7();
                await session.WriteAsync(state, TestContext.CancellationToken);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResetPendingAsync(vault, server, EnrollmentSecret(), TestContext.CancellationToken));
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomIdentityTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string EnrollmentSecret() => $"vle_{Guid.CreateVersion7():D}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    private async Task ChangeOwnerAsync(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("chown") { UseShellExecute = false };
        start.ArgumentList.Add("65534:65534"); start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Unable to start chown.");
        await process.WaitForExitAsync(TestContext.CancellationToken);
        Assert.AreEqual(0, process.ExitCode);
    }
    private sealed class RejectedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
