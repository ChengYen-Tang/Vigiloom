using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;

if (args.Length == 2 && args[0] == "--observe-process-pid" && int.TryParse(args[1], out var observedPid))
{
    var sample = Vigiloom.Agent.Core.Processes.ProcessReader.Read(CancellationToken.None);
    var process = sample.Items.Single(x => x.ProcessId == observedPid);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(process));
    return;
}
if (args.Length == 1 && args[0] == "--hold-for-inventory")
{
    Console.WriteLine("READY");
    await Console.In.ReadLineAsync();
    return;
}
// Test-only process: runs the production host with a strictly scoped temporary CA.
// No product TLS behavior or system trust store is modified.
var rootPath = Environment.GetEnvironmentVariable("VIGILOOM_TEST_CA") ?? throw new InvalidOperationException("Test CA is required.");
var mode = Environment.GetEnvironmentVariable("VIGILOOM_TEST_MODE") ?? throw new InvalidOperationException("Test mode is required.");
if (mode is not ("unavailable" or "drop-ack" or "normal")) throw new InvalidOperationException("Invalid test mode.");
using var root = X509CertificateLoader.LoadCertificateFromFile(rootPath);
var builder = AgentHost.CreateBuilder([]);
if (Environment.GetEnvironmentVariable("VIGILOOM_TEST_INVENTORY") != "enabled")
{
    // Replay tests deliberately isolate their queue; the inventory integration case opts in.
    foreach (var descriptor in builder.Services.Where(x => x.ImplementationType?.FullName == "Vigiloom.Agent.Hosting.Inventory.InventoryWorker").ToArray())
        builder.Services.Remove(descriptor);
}
builder.Services.AddHttpClient<IdentityClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentHeartbeatClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentSettingsClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentMetricSettingsClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentInventorySettingsClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<InventoryStatusClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentQueueCapacityClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentLogSettingsClient>(client => client.MaxResponseContentBufferSize = 512 * 1024).ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentBatchClient>().ConfigurePrimaryHttpMessageHandler(() => new InjectBatchResponse(mode) { InnerHandler = CreateHandler() });
using var host = builder.Build();
await host.RunAsync();

SocketsHttpHandler CreateHandler() => new()
{
    AllowAutoRedirect = false,
    UseCookies = false,
    SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate }
};

bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
{
    if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
    using var leaf = new X509Certificate2(certificate);
    using var verified = new X509Chain();
    verified.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    verified.ChainPolicy.CustomTrustStore.Add(root);
    verified.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    verified.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
    return verified.Build(leaf);
}

public sealed class InjectBatchResponse(string mode) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (mode == "unavailable") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var response = await base.SendAsync(request, token);
        if (mode == "drop-ack" && response.StatusCode == HttpStatusCode.NoContent)
        {
            response.Dispose();
            Console.WriteLine("TEST_ACK_DROPPED");
            throw new HttpRequestException("Injected lost acknowledgement after server commit.");
        }
        return response;
    }
}

public sealed class AgentHostHarnessMarker;
