using System.Net;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class BaselineSettingsClientTests(TestContext context)
{
    [TestMethod]
    public async Task Sync_KeepsLastGoodPolicyWhenServerIsUnavailable()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomBaselineSettingsTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var check = new BaselineCheck("shadow", "Shadow permissions", "linux", ["ubuntu:24.04"],
                "file-permissions-no-extended-acl", "/etc/shadow", "0640-or-stricter", "https://example.test/rule", "BSD-3-Clause");
            var policy = BaselineSettingsSnapshot.Create(21600, [new(Guid.CreateVersion7(), Guid.CreateVersion7(), "AAAAAAAAAAA=", check)]);
            var handler = new SettingsHandler { Policy = policy };
            using var http = new HttpClient(handler);
            var client = new AgentBaselineSettingsClient(http, TimeProvider.System);

            Assert.AreEqual(AgentSettingsSyncStatus.Saved, (await client.SyncAsync(vault, server, token)).Status);
            handler.Available = false;
            Assert.AreEqual(AgentSettingsSyncStatus.Unavailable, (await client.SyncAsync(vault, server, token)).Status);
            var saved = await client.LoadSavedAsync(vault, server, token);
            Assert.AreEqual(policy.Revision, saved.Settings!.Snapshot.Revision);
            Assert.AreEqual(id, saved.Settings.AgentId);
            handler.Status = HttpStatusCode.NotFound;
            Assert.AreEqual(AgentSettingsSyncStatus.Unsupported, (await client.SyncAsync(vault, server, token)).Status);
            Assert.AreEqual(policy.Revision, (await client.LoadSavedAsync(vault, server, token)).Settings!.Snapshot.Revision);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SettingsHandler : HttpMessageHandler
    {
        public required BaselineSettingsSnapshot Policy { get; init; }
        public bool Available { get; set; } = true;
        public HttpStatusCode? Status { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Status is { } status ? new HttpResponseMessage(status) : Available ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Policy) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
