using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReleaseOriginIntegrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task SignedPublisher_ThroughPrivateOriginAndTls_ReachesCentralCache() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var root = FindRepository();
            var python = Environment.GetEnvironmentVariable("VIGILOOM_RELEASE_PYTHON") ?? "python";
            var temporary = Path.Combine(Path.GetTempPath(), $"vigiloom-origin-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporary);
            Process? origin = null;
            WebApplication? proxy = null;
            Task<string>? originStdout = null, originStderr = null;
            Exception? failure = null;
            try
            {
                await RunPreparationAsync(python, root, temporary, token);
                await using (var resources = new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                    .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options))
                    await resources.Database.MigrateAsync(token);
                var factory = new Factory(database.ConnectionString);
                await using (var agentDb = factory.CreateDbContext()) await agentDb.Database.MigrateAsync(token);
                await using (var operations = new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
                    .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options))
                    await operations.Database.MigrateAsync(token);

                const string readToken = "isolated-test-origin-credential-0123456789abcdef";
                var port = AvailablePort();
                origin = StartPython(python, Path.Combine(root, "tools", "agent-release", "origin.py"),
                    ["--store", Path.Combine(temporary, "store"), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)], readToken);
                originStderr = origin.StandardError.ReadToEndAsync(CancellationToken.None);
                originStdout = origin.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var originUri = new Uri($"http://127.0.0.1:{port}/");
                using var upstream = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
                await WaitForOriginAsync(origin, upstream, originUri, token);

                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder();
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                var pfx = generated.Export(X509ContentType.Pfx);
                using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
                CryptographicOperations.ZeroMemory(pfx);
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                proxy = builder.Build();
                proxy.Run(async context =>
                {
                    using var outgoing = new HttpRequestMessage(HttpMethod.Get, new Uri(originUri, context.Request.Path.Value!.TrimStart('/')));
                    if (context.Request.Headers.TryGetValue("Authorization", out var auth)) outgoing.Headers.TryAddWithoutValidation("Authorization", auth.ToArray());
                    using var response = await upstream.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    context.Response.ContentLength = response.Content.Headers.ContentLength;
                    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                });
                await proxy.StartAsync(token);
                var address = proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                var baseUri = new Uri(address.TrimEnd('/') + "/");

                using (var untrusted = new HttpClient(new HttpClientHandler { UseProxy = false }))
                    await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync(new Uri(baseUri, "metadata/timestamp.json"), token));
                var expectedCertificate = certificate.RawData;
                using (var wrongPin = PinnedClient(RandomNumberGenerator.GetBytes(expectedCertificate.Length)))
                    await Assert.ThrowsAsync<HttpRequestException>(() => wrongPin.GetAsync(new Uri(baseUri, "metadata/timestamp.json"), token));
                using var client = PinnedClient(expectedCertificate);
                using (var wrong = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "targets/catalog.json")))
                {
                    wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "incorrect-test-token");
                    using var rejected = await client.SendAsync(wrong, token);
                    Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
                }

                var actor = Guid.CreateVersion7();
                var audit = new OperationAuditWriter();
                var sourceSettings = new AgentPackageSourceSettings(factory, audit, new EphemeralDataProtectionProvider(), TimeProvider.System);
                var source = await sourceSettings.UpdateAsync(new(baseUri.AbsoluteUri, PackageCredentialAction.Replace, readToken, ""), actor, token);
                Assert.IsNotNull(source);
                var trust = new UpdateTrustStore(factory, audit);
                var bootstrap = await File.ReadAllBytesAsync(Path.Combine(temporary, "bootstrap", "bootstrap.json"), token);
                await trust.ProvisionForAdministratorAsync(bootstrap, Convert.ToHexStringLower(SHA256.HashData(bootstrap)), actor, token);
                var metadata = new Uri(baseUri, "metadata/");
                var targets = new Uri(baseUri, "targets/");
                var repository = new BoundedTufRepository(metadata, targets, client, agentCredential: readToken);
                await trust.RefreshCatalogueAsync(new TrustedCatalogueReader(metadata, targets, repository), TimeProvider.System,
                    Convert.FromBase64String(source.Version), actor, token);
                Guid packageId;
                string archiveHash, manifestHash;
                await using (var db = factory.CreateDbContext())
                {
                    var packages = await db.Packages.AsNoTracking().ToArrayAsync(token);
                    Assert.HasCount(3, packages);
                    var package = packages.Single(x => x.RuntimeIdentifier == "win-x64");
                    Assert.AreEqual("1.0.0", package.Version);
                    Assert.IsTrue(package.Listed);
                    Assert.IsFalse(package.Revoked);
                    packageId = package.Id; archiveHash = package.Sha256; manifestHash = package.ManifestSha256;
                }
                var cachePath = Path.Combine(temporary, "cache");
                var transport = new PackageLibraryTransport(cachePath, 5 * 1024 * 1024, TimeProvider.System, client);
                var library = new PackageLibrary(factory, sourceSettings, trust, transport, TimeProvider.System);
                Assert.IsTrue(await library.CacheAsync(packageId, token));
                var cached = await File.ReadAllBytesAsync(Path.Combine(cachePath, archiveHash + ".pkg"), token);
                CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(temporary, "artifacts", "agent-win-x64.zip"), token), cached);
                Assert.AreEqual(archiveHash, Convert.ToHexStringLower(SHA256.HashData(cached)));
                var manifest = await File.ReadAllBytesAsync(Path.Combine(temporary, "store", "generations",
                    (await File.ReadAllTextAsync(Path.Combine(temporary, "store", "current"), token)).Trim(), "targets", manifestHash + ".json"), token);
                Assert.AreEqual(manifestHash, Convert.ToHexStringLower(SHA256.HashData(manifest)));
                Assert.IsFalse(origin.HasExited, "The origin stopped unexpectedly.");
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                var cleanupErrors = new List<Exception>();
                if (proxy is not null)
                {
                    try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await proxy.StopAsync(deadline.Token); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                    try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await proxy.DisposeAsync().AsTask().WaitAsync(deadline.Token); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                var originStopped = origin is null;
                if (origin is not null)
                {
                    try
                    {
                        if (!origin.HasExited) origin.Kill(entireProcessTree: true);
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await origin.WaitForExitAsync(deadline.Token);
                        originStopped = true;
                        if (originStdout is not null) await originStdout.WaitAsync(deadline.Token);
                        if (originStderr is not null) await originStderr.WaitAsync(deadline.Token);
                    }
                    catch (Exception error) { cleanupErrors.Add(error); }
                    finally { origin.Dispose(); }
                }
                if (originStopped && cleanupErrors.Count == 0 && failure is not PreparationProcessCleanupException)
                {
                    try { DeleteTemporary(temporary); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                else cleanupErrors.Add(new InvalidOperationException($"Test files retained for inspection: {temporary}"));
                if (cleanupErrors.Count > 0)
                {
                    if (failure is not null) cleanupErrors.Insert(0, failure);
                    throw new AggregateException("Integration test cleanup failed.", cleanupErrors);
                }
            }
        }, context.CancellationToken);

    private static async Task RunPreparationAsync(string python, string root, string directory, CancellationToken token)
    {
        using var process = StartPython(python, Path.Combine(root, "tests", "Fixtures", "AgentRelease", "prepare.py"),
            [Path.Combine(root, "tools", "agent-release"), directory], null);
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException cancellation)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (Exception cleanup) { throw new PreparationProcessCleanupException(cancellation, cleanup); }
            throw;
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Test publisher preparation failed: {await stderr}");
        _ = await stdout;
    }

    private static Process StartPython(string python, string script, string[] arguments, string? token)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (token is not null) start.Environment["VIGILOOM_ORIGIN_READ_TOKEN"] = token;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the test origin.");
    }

    private static async Task WaitForOriginAsync(Process process, HttpClient client, Uri origin, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (process.HasExited) throw new InvalidOperationException("The test origin exited before becoming ready.");
            try { using var response = await client.GetAsync(new Uri(origin, "metadata/root.json"), timeout.Token); if (response.StatusCode == HttpStatusCode.Unauthorized) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static HttpClient PinnedClient(byte[] expectedCertificate)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, peer, _, errors) => peer is not null &&
                (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && peer.RawData.AsSpan().SequenceEqual(expectedCertificate) };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools", "agent-release", "origin.py"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the Agent release test fixtures.");
    }

    private static void DeleteTemporary(string directory)
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), parent, comparison) ||
            !Path.GetFileName(full).StartsWith("vigiloom-origin-test-", StringComparison.Ordinal) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to remove an unexpected test directory.");
        Directory.Delete(full, recursive: true);
    }

    private sealed class Factory(string connectionString) : IDbContextFactory<AgentManagementDbContext>
    {
        public AgentManagementDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentManagementDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options);
    }

    private sealed class PreparationProcessCleanupException(Exception cancellation, Exception cleanup)
        : AggregateException("Test publisher could not be stopped safely.", cancellation, cleanup);
}
