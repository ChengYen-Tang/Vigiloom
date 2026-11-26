using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Web.Diagnostics;
using Vigiloom.Web.Operations;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteHostAuditTests(TestContext context)
{
    [TestMethod]
    public async Task AgentIdentityUsesOperationsHistoryAfterResourcesCutover()
    {
        var now = DateTimeOffset.UtcNow;
        var query = new OperationAuditQuery(now.AddMinutes(-1), now.AddMinutes(1));
        var device = Guid.CreateVersion7(); var agent = Guid.CreateVersion7(); var actor = Guid.CreateVersion7(); var package = Guid.CreateVersion7();
        var entries = new OperationAuditEntry[]
        {
            new(now, agent, device, null, AgentRegistration: new(AgentRegistrationAction.AgentEnrolled, agent, true)),
            new(now.AddSeconds(-1), actor, device, null, AgentAssignment: new(agent, package, null))
        };
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("/internal/v1/audit/agent-registration", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OperationAuditPage(entries, 1, false, false)) });
        })) { BaseAddress = new Uri("http://operations/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Resources:Url"] = "http://resources", ["Services:Operations:Url"] = "http://operations" }).Build();
        using var services = new ServiceCollection().AddSingleton(new RemoteAuditQuery(client))
            .AddSingleton(provider => new AuditQueryReader(new OperationAuditQueryService(null!), config, provider)).AddSingleton<AgentIdentityAuditFeed>().BuildServiceProvider();
        var reader = new AuditFeedReader(null!, null!, null!, null!, null!, config, services, null!);
        var page = await reader.QueryAsync(new ClaimsPrincipal(), AuditFeedSource.AgentIdentity, query, context.CancellationToken);
        Assert.HasCount(2, page.Items);
        Assert.AreEqual(AuditFeedAction.AgentEnrolled, page.Items[0].Action);
        Assert.IsNull(page.Items[0].ActorId);
        Assert.AreEqual(agent, page.Items[0].AgentId);
        Assert.AreEqual(AuditFeedAction.AgentVersionAssignmentCancelled, page.Items[1].Action);
        Assert.AreEqual(actor, page.Items[1].ActorId);
        Assert.AreEqual(package, page.Items[1].AgentVersion!.PreviousPackageId);
    }

    [TestMethod]
    public async Task AuditReaderRoutesHostSourcesToHostService()
    {
        var query = new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var expected = new AuditFeedPage(AuditFeedSource.AgentMetricSettings, [], 1, false, false);
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.AreEqual("/internal/v1/audit/query", request.RequestUri!.AbsolutePath);
            var input = await request.Content!.ReadFromJsonAsync<AuditFeedRequest>(context.CancellationToken);
            Assert.AreEqual(expected.Source, input!.Source);
            Assert.AreEqual(query, input.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        })) { BaseAddress = new Uri("http://host/") };
        using var services = new ServiceCollection().AddSingleton(new RemoteHostAuditFeed(client)).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:HostMonitoring:Url"] = "http://host" }).Build();
        var reader = new AuditFeedReader(null!, null!, null!, null!, null!, config, services, null!);
        var actual = await reader.QueryAsync(new ClaimsPrincipal(), expected.Source, query, context.CancellationToken);
        Assert.AreEqual(expected.Source, actual.Source);
        Assert.AreEqual(expected.Page, actual.Page);
    }

    [TestMethod]
    public async Task AuditReaderRoutesInventoryToSecurityService()
    {
        var query = new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var expected = new AuditFeedPage(AuditFeedSource.InventorySettings, [], 1, false, false);
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.AreEqual("/internal/v1/inventory-settings/audit", request.RequestUri!.AbsolutePath);
            Assert.AreEqual(query, await request.Content!.ReadFromJsonAsync<OperationAuditQuery>(context.CancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        })) { BaseAddress = new Uri("http://security/") };
        using var services = new ServiceCollection().BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:Security:Url"] = "http://security" }).Build();
        var reader = new AuditFeedReader(null!, null!, null!, null!, null!, config, services, new ClientFactory(client));
        Assert.AreEqual(expected.Source, (await reader.QueryAsync(new ClaimsPrincipal(), expected.Source, query, context.CancellationToken)).Source);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
