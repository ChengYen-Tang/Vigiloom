using System.Net;
using System.Text;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class InventoryStatusClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Read_RequiresAuthenticatedValidCentralStatus()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryStatusTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var handler = new StatusHandler();
            using var http = new HttpClient(handler);
            var client = new InventoryStatusClient(http, TimeProvider.System);

            handler.Body = "[]";
            var empty = await client.ReadAsync(vault, server, token);
            Assert.AreEqual(InventoryStatusResultKind.Success, empty.Kind);
            Assert.IsEmpty(empty.Sources!);
            Assert.IsFalse(empty.SupportsProcessSources);
            Assert.AreEqual("/api/v1/agent/inventory/status", handler.Path);
            Assert.AreEqual("?contract=5", handler.Query);
            Assert.StartsWith($"vla_{id:D}.", handler.Bearer);

            handler.Body = "[{\"source\":13,\"highWaterGeneration\":1}]";
            Assert.AreEqual(InventoryStatusResultKind.Invalid, (await client.ReadAsync(vault, server, token)).Kind);
            handler.AdvertiseProcessSources = true;
            var extended = await client.ReadAsync(vault, server, token);
            Assert.AreEqual(InventoryStatusResultKind.Success, extended.Kind);
            Assert.IsTrue(extended.SupportsProcessSources);
            Assert.AreEqual(InventorySource.HostProcesses, extended.Sources![0].Source);
            handler.AdvertiseProcessSources = false;

            handler.Body = "[{\"source\":3,\"highWaterGeneration\":7,\"activeGeneration\":7,\"lastAttemptGeneration\":6,\"lastOutcome\":1,\"lastStatus\":2}]";
            var result = await client.ReadAsync(vault, server, token);
            Assert.AreEqual(InventoryStatusResultKind.Success, result.Kind);
            Assert.AreEqual(InventorySource.LinuxDpkg, result.Sources![0].Source);
            Assert.AreEqual(7L, result.Sources[0].HighWaterGeneration);
            Assert.AreEqual(id, result.Owner!.AgentId);

            handler.Body = "[{\"source\":3,\"highWaterGeneration\":7,\"activeGeneration\":7,\"lastAttemptGeneration\":7,\"lastOutcome\":1,\"lastStatus\":2}]";
            Assert.AreEqual(InventoryStatusResultKind.Invalid, (await client.ReadAsync(vault, server, token)).Kind);
            handler.Body = "[{\"source\":3,\"highWaterGeneration\":7,\"lastAttemptGeneration\":7,\"lastOutcome\":2,\"lastStatus\":2}]";
            Assert.AreEqual(InventoryStatusResultKind.Invalid, (await client.ReadAsync(vault, server, token)).Kind);

            handler.Body = "[{\"source\":3,\"highWaterGeneration\":1},{\"source\":3,\"highWaterGeneration\":1}]";
            Assert.AreEqual(InventoryStatusResultKind.Invalid, (await client.ReadAsync(vault, server, token)).Kind);
            handler.Body = "[{\"source\":3,\"highWaterGeneration\":0}]";
            Assert.AreEqual(InventoryStatusResultKind.Invalid, (await client.ReadAsync(vault, server, token)).Kind);
            handler.Status = HttpStatusCode.ServiceUnavailable;
            Assert.AreEqual(InventoryStatusResultKind.Unavailable, (await client.ReadAsync(vault, server, token)).Kind);
            handler.Status = HttpStatusCode.Unauthorized;
            Assert.AreEqual(InventoryStatusResultKind.Unauthorized, (await client.ReadAsync(vault, server, token)).Kind);
            handler.Status = HttpStatusCode.OK;
            handler.Body = "[]";
            handler.BeforeResponse = async () =>
            {
                await using var session = vault.Acquire();
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = Guid.CreateVersion7(),
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            };
            Assert.AreEqual(InventoryStatusResultKind.OwnerChanged, (await client.ReadAsync(vault, server, token)).Kind);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "[]";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Path { get; private set; }
        public string? Query { get; private set; }
        public string? Bearer { get; private set; }
        public bool AdvertiseProcessSources { get; set; }
        public Func<Task>? BeforeResponse { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Query = request.RequestUri?.Query;
            Bearer = request.Headers.Authorization?.Parameter;
            if (BeforeResponse is not null) await BeforeResponse();
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
            if (AdvertiseProcessSources) response.Headers.Add("Vigiloom-Inventory-Contract", "5");
            return response;
        }
    }
}
