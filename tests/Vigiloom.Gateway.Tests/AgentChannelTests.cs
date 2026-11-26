extern alias HostMonitoring;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Work;
using System.Text.Json;

namespace Vigiloom.Gateway.Tests;

[TestClass]
public sealed class AgentChannelTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task BackplaneDeliversToAgentConnectedToAnotherInstance()
    {
        var redis = Environment.GetEnvironmentVariable("VIGILOOM_TEST_REDIS");
        if (string.IsNullOrEmpty(redis)) Assert.Inconclusive("Set VIGILOOM_TEST_REDIS to run the multi-instance integration test.");
        var agent = Guid.CreateVersion7();
        using var first = CreateApp(agent, redis);
        using var second = CreateApp(agent, redis);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Connect(firstClient.BaseAddress!, "first");
        connection.On("UpdateAvailable", () => received.TrySetResult());
        await connection.StartAsync(TestContext.CancellationToken);
        var handler = new HostMonitoring::Vigiloom.HostMonitoring.AgentChangeHandler(
            second.Services.GetRequiredService<IHubContext<HostMonitoring::Vigiloom.HostMonitoring.AgentChannel>>());
        await handler.ExecuteAsync(new ServiceWork(nameof(AgentAssignmentChanged),
            JsonSerializer.Serialize(new AgentAssignmentChanged(agent, Guid.CreateVersion7()))), TestContext.CancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
    }

    private static WebApplicationFactory<HostMonitoring::Program> CreateApp(Guid agent, string redis)
    {
        var app = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
        {
            web.UseUrls("http://127.0.0.1:0");
            web.UseSetting("Temporal:Enabled", "false");
            web.UseSetting("Hangfire:Enabled", "false");
            web.UseSetting("ConnectionStrings:SignalR", redis);
            web.ConfigureServices(services => services.AddHttpClient("resources")
                .ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(agent, Guid.CreateVersion7())));
        });
        app.UseKestrel(0);
        return app;
    }

    [TestMethod]
    public async Task AuthenticatedAgentReceivesOnlyItsOwnNotification()
    {
        var agent = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        using var app = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
        {
            web.UseUrls("http://127.0.0.1:0");
            web.UseSetting("Temporal:Enabled", "false");
            web.UseSetting("Hangfire:Enabled", "false");
            web.ConfigureServices(services => services.AddHttpClient("resources")
                .ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(agent, other)));
        });
        app.UseKestrel(0);
        using var http = app.CreateClient();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrongAgent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = Connect(http.BaseAddress!, "first");
        await using var second = Connect(http.BaseAddress!, "second");
        first.On("UpdateAvailable", () => received.TrySetResult());
        second.On("UpdateAvailable", () => wrongAgent.TrySetResult());
        await first.StartAsync(TestContext.CancellationToken);
        await second.StartAsync(TestContext.CancellationToken);
        var handler = new HostMonitoring::Vigiloom.HostMonitoring.AgentChangeHandler(
            app.Services.GetRequiredService<IHubContext<HostMonitoring::Vigiloom.HostMonitoring.AgentChannel>>());
        await handler.ExecuteAsync(new ServiceWork(nameof(AgentAssignmentChanged),
            JsonSerializer.Serialize(new AgentAssignmentChanged(agent, Guid.CreateVersion7()))), TestContext.CancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.IsFalse(wrongAgent.Task.IsCompleted);

        await using var rejected = Connect(http.BaseAddress!, "invalid");
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(async () => await rejected.StartAsync(TestContext.CancellationToken));
    }

    private static HubConnection Connect(Uri address, string credential) => new HubConnectionBuilder()
        .WithUrl(new Uri(address, "api/v1/agent/channel"), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.AccessTokenProvider = () => Task.FromResult<string?>(credential);
        }).Build();

    private sealed class IdentityHandler(Guid agent, Guid other) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var command = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
            var id = command?.Credential switch { "first" => agent, "second" => other, _ => Guid.Empty };
            return id == Guid.Empty ? new(HttpStatusCode.Unauthorized) : new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RegisteredAgentIdentity(id, Guid.CreateVersion7(), 1))
            };
        }
    }
}
