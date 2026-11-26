using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.AgentVersions;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteAgentOwnershipTests(TestContext context)
{
    [TestMethod]
    public async Task PreservesHistoricalLookupButOnlyAuthorizesCurrentActiveAgent()
    {
        var device = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var value = new DeviceAgentOwnership(new DeviceAgentState(device, 7, true), new(agent, 7, now, null));
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) })) { BaseAddress = new Uri("http://resources/") };
        var resolver = new RemoteAgentOwnership(client);
        Assert.AreEqual(agent, (await ((IDeviceInventoryAgent)resolver).ResolveAsync(device, context.CancellationToken)).AgentId);
        Assert.AreEqual(agent, (await ((IAgentOwnerResolver)resolver).ResolveAsync(device, context.CancellationToken))!.AgentId);
        value = value with { Registration = value.Registration! with { RevokedAt = now } };
        Assert.AreEqual(agent, (await ((IDeviceInventoryAgent)resolver).ResolveAsync(device, context.CancellationToken)).AgentId);
        Assert.IsNull(await ((IAgentOwnerResolver)resolver).ResolveAsync(device, context.CancellationToken));
        value = value with { Device = value.Device with { Eligible = false }, Registration = value.Registration! with { RevokedAt = null } };
        Assert.IsNull(await ((IAgentOwnerResolver)resolver).ResolveAsync(device, context.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
