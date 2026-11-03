using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;

// Test-only process: runs the production host with a strictly scoped temporary CA.
// No product TLS behavior or system trust store is modified.
var rootPath = Environment.GetEnvironmentVariable("VIGILOOM_TEST_CA") ?? throw new InvalidOperationException("Test CA is required.");
var mode = Environment.GetEnvironmentVariable("VIGILOOM_TEST_MODE") ?? throw new InvalidOperationException("Test mode is required.");
if (mode is not ("unavailable" or "drop-ack" or "normal")) throw new InvalidOperationException("Invalid test mode.");
using var root = X509CertificateLoader.LoadCertificateFromFile(rootPath);
var builder = AgentHost.CreateBuilder([]);
builder.Services.AddHttpClient<IdentityClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentHeartbeatClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentSettingsClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
builder.Services.AddHttpClient<AgentQueueCapacityClient>().ConfigurePrimaryHttpMessageHandler(CreateHandler);
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
