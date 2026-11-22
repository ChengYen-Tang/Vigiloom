using System.Net;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class IdentityRotationStagingTests(TestContext context)
{
    [TestMethod]
    public async Task RotationDoesNotFinishUntilUpdaterConfirmsCredentialStaging()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"identity-rotation-{Guid.CreateVersion7():N}");
        var server = new Uri("https://example.test/");
        var agent = Guid.CreateVersion7();
        var vault = new IdentityVault(directory);
        try
        {
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agent,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, context.CancellationToken);
            var handler = new RotationHandler();
            using var http = new HttpClient(handler);
            var client = new IdentityClient(http);
            string? firstPending = null;
            await Assert.ThrowsExactlyAsync<IOException>(() => client.RotateAsync(vault, server, context.CancellationToken,
                (_, pending, _) => { firstPending = pending; throw new IOException("Staging response was lost."); }));
            Assert.AreEqual(0, handler.FinishCount);
            await client.RotateAsync(vault, server, context.CancellationToken, (_, pending, _) =>
            {
                Assert.AreEqual(firstPending, pending);
                return Task.CompletedTask;
            });
            Assert.AreEqual(1, handler.FinishCount);
            await using var saved = vault.Acquire();
            Assert.IsNull((await saved.ReadAsync(context.CancellationToken))!.PendingSecret);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class RotationHandler : HttpMessageHandler
    {
        public int FinishCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/finish", StringComparison.Ordinal)) FinishCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
