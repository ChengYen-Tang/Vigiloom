using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Api;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentIdentityTlsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task AgentEnrollsOverValidatedTlsAndKestrelEnforcesBodyLimit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Vigiloom Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            using var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
            serverRequest.CertificateExtensions.Add(san.Build());
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var unsigned = serverRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
            using var serverCertificate = unsigned.CopyWithPrivateKey(serverKey);
            // Schannel requires a non-ephemeral key handle for a server certificate.
            var pfx = serverCertificate.Export(X509ContentType.Pfx);
            using var tlsCertificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
            CryptographicOperations.ZeroMemory(pfx);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }));
                builder.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(tlsCertificate)));
            });
            factory.UseKestrel(0); factory.StartServer();
            var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var listenUrl = new Uri(addresses.Single(x => x.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) + "/");
            var url = new UriBuilder(listenUrl) { Host = "localhost" }.Uri;
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var device = new Device("TLS test server", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            EnrollmentIssue issued;
            await using (var scope = factory.Services.CreateAsyncScope())
                issued = await scope.ServiceProvider.GetRequiredService<AgentIdentity>().IssueAsync(device.Id, Guid.CreateVersion7(), token);
            Assert.AreEqual(AgentIdentityStatus.Success, issued.Status);
            var statePath = Path.Combine(Path.GetTempPath(), "VigiloomAgentTlsTests", Path.GetRandomFileName());
            try
            {
                using var untrusted = new HttpClient();
                await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync(new Uri(url, "/health/live"), token));
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                    return chain.Build(certificate);
                };
                using var http = new HttpClient(handler) { MaxResponseContentBufferSize = 65536 };
                await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri(listenUrl, "/health/live"), token));
                var vault = new IdentityVault(statePath);
                var id = await new IdentityClient(http).EnrollAsync(vault, url, issued.Secret, token);
                await using (var session = vault.Acquire())
                {
                    var state = await session.ReadAsync(token);
                    Assert.IsNotNull(state);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{id:D}.{state.MachineSecret}");
                }
                using (var identity = await http.GetAsync(new Uri(url, "/api/v1/agent/identity"), token))
                {
                    Assert.AreEqual(HttpStatusCode.OK, identity.StatusCode);
                    Assert.AreEqual(device.Id, (await identity.Content.ReadFromJsonAsync<AgentIdentityResponse>(cancellationToken: token))!.DeviceId);
                }
                Assert.AreEqual(HeartbeatResult.ServerMismatch, await new AgentHeartbeatClient(http).SendAsync(vault, new Uri("https://different.example/"), token));
                Assert.AreEqual(HeartbeatResult.Accepted, await new AgentHeartbeatClient(http).SendAsync(vault, url, token));
                await using (var db = await agents.CreateDbContextAsync(token))
                    Assert.IsNotNull((await db.Agents.SingleAsync(x => x.Id == id, token)).LastHeartbeatAt);
                using (var oversized = await http.PostAsync(new Uri(url, "/api/v1/agent/enroll"), new StringContent(new string('x', 4096), System.Text.Encoding.UTF8, "application/json"), token))
                    Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
                using (var oversizedChunked = await http.PostAsync(new Uri(url, "/api/v1/agent/enroll"), new UnknownLengthContent(4096), token))
                    Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversizedChunked.StatusCode);
            }
            finally
            {
                var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomAgentTlsTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(statePath).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(statePath)) Directory.Delete(statePath, recursive: true);
            }
        }, TestContext.CancellationToken);

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly int length;
        public UnknownLengthContent(int length) { this.length = length; Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        protected override bool TryComputeLength(out long value) { value = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[length]).AsTask();
    }
}
