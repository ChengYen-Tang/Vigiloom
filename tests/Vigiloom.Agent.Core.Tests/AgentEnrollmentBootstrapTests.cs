using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentEnrollmentBootstrapTests(TestContext context)
{
    [TestMethod]
    public async Task RetriesPendingIdentityAndRemovesBootstrapAfterRegistration()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"bootstrap-{Guid.NewGuid():N}");
        var server = new Uri("https://example.test/");
        var secret = Secret();
        try
        {
            var vault = new IdentityVault(root);
            await WriteBootstrapAsync(vault, secret);
            var id = Guid.CreateVersion7();
            var handler = new EnrollmentHandler(id) { FailFirst = true };
            using var http = new HttpClient(handler);
            var bootstrap = new AgentEnrollmentBootstrap(new IdentityClient(http));
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => bootstrap.TryOnceAsync(vault, server, context.CancellationToken));
            string machine;
            await using (var session = vault.Acquire())
            {
                var pending = await session.ReadAsync(context.CancellationToken);
                Assert.IsNotNull(pending);
                Assert.AreEqual(secret, pending.EnrollmentSecret);
                machine = pending.MachineSecret;
            }
            Assert.IsTrue(File.Exists(Path.Combine(root, AgentEnrollmentBootstrap.FileName)));
            Assert.IsTrue(await bootstrap.TryOnceAsync(vault, server, context.CancellationToken));
            await using (var session = vault.Acquire())
            {
                var registered = await session.ReadAsync(context.CancellationToken);
                Assert.IsNotNull(registered);
                Assert.AreEqual(id, registered.AgentId);
                Assert.AreEqual(machine, registered.MachineSecret);
                Assert.IsNull(registered.EnrollmentSecret);
            }
            Assert.IsFalse(File.Exists(Path.Combine(root, AgentEnrollmentBootstrap.FileName)));
            Assert.IsFalse(await bootstrap.TryOnceAsync(vault, server, context.CancellationToken));
            Assert.AreEqual(2, handler.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DifferentPendingEnrollmentCannotBeReplaced()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"bootstrap-{Guid.NewGuid():N}");
        var server = new Uri("https://example.test/");
        try
        {
            var vault = new IdentityVault(root);
            var original = Secret();
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, EnrollmentSecret = original,
                    MachineSecret = new string('a', 43) }, context.CancellationToken);
            await WriteBootstrapAsync(vault, Secret());
            var handler = new EnrollmentHandler(Guid.CreateVersion7());
            using var http = new HttpClient(handler);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                new AgentEnrollmentBootstrap(new IdentityClient(http)).TryOnceAsync(vault, server, context.CancellationToken));
            await using (var session = vault.Acquire()) Assert.AreEqual(original, (await session.ReadAsync(context.CancellationToken))!.EnrollmentSecret);
            Assert.IsTrue(File.Exists(Path.Combine(root, AgentEnrollmentBootstrap.FileName)));
            Assert.AreEqual(0, handler.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task AlreadyRegisteredAgentRemovesInterruptedBootstrapWithoutReregistering()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"bootstrap-{Guid.NewGuid():N}");
        var server = new Uri("https://example.test/");
        try
        {
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = Guid.CreateVersion7(),
                    MachineSecret = new string('a', 43) }, context.CancellationToken);
            await WriteBootstrapAsync(vault, Secret());
            var handler = new EnrollmentHandler(Guid.CreateVersion7());
            using var http = new HttpClient(handler);
            Assert.IsTrue(await new AgentEnrollmentBootstrap(new IdentityClient(http)).TryOnceAsync(vault, server, context.CancellationToken));
            Assert.IsFalse(File.Exists(Path.Combine(root, AgentEnrollmentBootstrap.FileName)));
            Assert.AreEqual(0, handler.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task RejectsBootstrapReadableByOtherUsers()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"bootstrap-{Guid.NewGuid():N}");
        try
        {
            var vault = new IdentityVault(root);
            await WriteBootstrapAsync(vault, Secret());
            var path = Path.Combine(root, AgentEnrollmentBootstrap.FileName);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            var handler = new EnrollmentHandler(Guid.CreateVersion7());
            using var http = new HttpClient(handler);
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                new AgentEnrollmentBootstrap(new IdentityClient(http)).TryOnceAsync(vault, new Uri("https://example.test/"), context.CancellationToken));
            Assert.AreEqual(0, handler.Requests);
            Assert.IsTrue(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private async Task WriteBootstrapAsync(IdentityVault vault, string secret)
    {
        await using var session = vault.Acquire();
        var path = Path.Combine(vault.StateDirectory, AgentEnrollmentBootstrap.FileName);
        await File.WriteAllTextAsync(path, secret, context.CancellationToken);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string Secret() => $"vle_{Guid.CreateVersion7():D}.{new string('a', 43)}";

    private sealed class EnrollmentHandler(Guid agentId) : HttpMessageHandler
    {
        public bool FailFirst { get; init; }
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (++Requests == 1 && FailFirst) throw new HttpRequestException("Simulated temporary outage.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = agentId }) });
        }
    }
}
