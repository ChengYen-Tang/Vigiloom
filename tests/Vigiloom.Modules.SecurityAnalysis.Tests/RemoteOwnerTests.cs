using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Security;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
public sealed class RemoteOwnerTests
{
    [TestMethod]
    public async Task OwnerVerificationReflectsRevocationAndGeneration()
    {
        var device = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        var handler = new OwnershipHandler(new(new(device, 3, true), new(agent, 3, DateTimeOffset.UtcNow, null)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://resources/") };
        var sources = new ResourceDevices(client);
        var expected = new InventoryOwner(agent, device, 3);
        Assert.AreEqual(expected, await sources.ResolveAsync(device, CancellationToken.None));
        await using var connection = new SqlConnection();
        Assert.IsTrue(await sources.LockCurrentAsync(connection, null!, expected, CancellationToken.None));
        handler.Current = handler.Current with { Device = handler.Current.Device with { Generation = 4 } };
        Assert.IsFalse(await sources.LockCurrentAsync(connection, null!, expected, CancellationToken.None));
        handler.Current = handler.Current with { Device = handler.Current.Device with { Eligible = false } };
        Assert.IsNull(await sources.ResolveAsync(device, CancellationToken.None));
        handler.Current = handler.Current with { Device = handler.Current.Device with { Eligible = true }, Registration = handler.Current.Registration! with { RevokedAt = DateTimeOffset.UtcNow } };
        Assert.IsNull(await sources.ResolveAsync(device, CancellationToken.None));
    }

    private sealed class OwnershipHandler(DeviceAgentOwnership current) : HttpMessageHandler
    {
        public DeviceAgentOwnership Current { get; set; } = current;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Current) });
    }
}
