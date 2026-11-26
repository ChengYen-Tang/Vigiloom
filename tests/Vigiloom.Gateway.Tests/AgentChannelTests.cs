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
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Work;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

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
    public async Task GatewayForwardsAgentNotificationsAfterReconnect()
    {
        var agent = Guid.CreateVersion7();
        using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
        {
            web.UseUrls("http://127.0.0.1:0");
            web.UseSetting("Temporal:Enabled", "false");
            web.UseSetting("Hangfire:Enabled", "false");
            web.ConfigureServices(services => services.AddHttpClient("resources")
                .ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(agent, Guid.CreateVersion7())));
        });
        host.UseKestrel(0);
        using var hostClient = host.CreateClient();
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = generated.Export(X509ContentType.Pfx);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = hostClient.BaseAddress!.ToString() })));
        gateway.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        using var gatewayClient = gateway.CreateClient();
        var gatewayAddress = new Uri(gateway.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var probe = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, peer, _, errors) => peer is not null &&
                (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                CryptographicOperations.FixedTimeEquals(peer.GetCertHash(HashAlgorithmName.SHA256), certificate.GetCertHash(HashAlgorithmName.SHA256))
        });
        using var probeResult = await probe.GetAsync(new Uri(gatewayAddress, "/internal"), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, probeResult.StatusCode);
        var handler = new HostMonitoring::Vigiloom.HostMonitoring.AgentChangeHandler(
            host.Services.GetRequiredService<IHubContext<HostMonitoring::Vigiloom.HostMonitoring.AgentChannel>>());
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = first;
        await using var connection = new HubConnectionBuilder().WithUrl(new Uri(gatewayAddress, "api/v1/agent/channel"), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.AccessTokenProvider = () => Task.FromResult<string?>("first");
            options.WebSocketConfiguration = socket =>
            {
                socket.Proxy = null;
                socket.RemoteCertificateValidationCallback = (_, peer, _, errors) =>
                    peer is not null && (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                    CryptographicOperations.FixedTimeEquals(peer.GetCertHash(HashAlgorithmName.SHA256), certificate.GetCertHash(HashAlgorithmName.SHA256));
            };
        }).Build();
        connection.On("UpdateAvailable", () => receipt.TrySetResult());
        await connection.StartAsync(TestContext.CancellationToken);
        Assert.AreEqual(401, await connection.InvokeCoreAsync<int>("Heartbeat", ["invalid", null], TestContext.CancellationToken));
        await NotifyAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        await connection.StopAsync(TestContext.CancellationToken);
        receipt = second;
        await connection.StartAsync(TestContext.CancellationToken);
        Assert.AreEqual(401, await connection.InvokeCoreAsync<int>("Heartbeat", ["invalid", null], TestContext.CancellationToken));
        await NotifyAsync();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        return;

        Task NotifyAsync() => handler.ExecuteAsync(new ServiceWork(nameof(AgentAssignmentChanged),
            JsonSerializer.Serialize(new AgentAssignmentChanged(agent, Guid.CreateVersion7()))), TestContext.CancellationToken);
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
        var scanReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrongAgent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = Connect(http.BaseAddress!, "first");
        await using var second = Connect(http.BaseAddress!, "second");
        first.On("UpdateAvailable", () => received.TrySetResult());
        first.On("ScanAvailable", () => scanReceived.TrySetResult());
        second.On("UpdateAvailable", () => wrongAgent.TrySetResult());
        await first.StartAsync(TestContext.CancellationToken);
        await second.StartAsync(TestContext.CancellationToken);
        var handler = new HostMonitoring::Vigiloom.HostMonitoring.AgentChangeHandler(
            app.Services.GetRequiredService<IHubContext<HostMonitoring::Vigiloom.HostMonitoring.AgentChannel>>());
        var device = Guid.CreateVersion7();
        await handler.ExecuteAsync(new ServiceWork(nameof(AgentDeviceChanged),
            JsonSerializer.Serialize(new AgentDeviceChanged(device, [1]))), TestContext.CancellationToken);
        await handler.ExecuteAsync(new ServiceWork(nameof(AgentRegistrationChanged),
            JsonSerializer.Serialize(new AgentRegistrationChanged(agent, device, true))), TestContext.CancellationToken);
        await handler.ExecuteAsync(new ServiceWork(nameof(AgentAssignmentChanged),
            JsonSerializer.Serialize(new AgentAssignmentChanged(agent, Guid.CreateVersion7()))), TestContext.CancellationToken);
        await handler.ExecuteAsync(new ServiceWork(nameof(YaraScanChanged), JsonSerializer.Serialize(new YaraScanChanged(agent))), TestContext.CancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        await scanReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
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
