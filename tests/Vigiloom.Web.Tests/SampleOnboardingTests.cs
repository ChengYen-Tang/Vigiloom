using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Web.Publishing;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class SampleOnboardingTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task Sample_ReportsOnlyAfterApprovalAndSupportsHealthChecks() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        certificateRequest.CertificateExtensions.Add(names.Build());
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = generated.Export(X509ContentType.Pfx);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null);
        CryptographicOperations.ZeroMemory(pfx);
        var certificateFile = Path.Combine(Path.GetTempPath(), $"vigiloom-sample-ca-{Guid.CreateVersion7():N}.pem");
        await File.WriteAllTextAsync(certificateFile, certificate.ExportCertificatePem(), token);
        try
        {
            using var original = PingTestSite.CreateFactory(database.ConnectionString);
            using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<PublicStreamAdmission>();
                services.AddSingleton(new PublicStreamAdmission(64, TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(5), TimeProvider.System));
            }));
            factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            factory.StartServer();
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var source = await services.GetRequiredService<SourceCredentials>().IssueAsync("sample source", token);
            var discovery = services.GetRequiredService<CandidateDiscovery>();
            var address = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var samplePort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
            var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/Vigiloom.Sample.AspNetCore/bin", configuration, "net10.0/Vigiloom.Sample.AspNetCore.dll"));
            Assert.IsTrue(File.Exists(sample), "Sample application must be built with the tests.");
            var runtimeRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))!.Parent!.Parent!.FullName;
            var start = new ProcessStartInfo(Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(sample);
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{samplePort}";
            start.Environment["VIGILOOM_SAMPLE_INSTANCE_ID"] = "sample-test-1";
            start.Environment["VIGILOOM_SAMPLE_ENVIRONMENT"] = "prod";
            start.Environment["OTEL_SERVICE_NAME"] = "orders";
            start.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
            start.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"https://localhost:{address.Port}/api/v1/otlp";
            start.Environment["OTEL_EXPORTER_OTLP_CERTIFICATE"] = certificateFile;
            start.Environment["OTEL_EXPORTER_OTLP_HEADERS"] = $"Authorization=Bearer%20{source.Secret}";
            start.Environment["OTEL_TRACES_SAMPLER"] = "always_on";
            start.Environment["OTEL_METRIC_EXPORT_INTERVAL"] = "1000";
            start.Environment["OTEL_BLRP_SCHEDULE_DELAY"] = "1000";
            start.Environment["OTEL_BSP_SCHEDULE_DELAY"] = "1000";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the sample.");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            Exception? failure = null;
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{samplePort}"), Timeout = TimeSpan.FromSeconds(2) };
                async Task<bool> SendRequest()
                {
                    if (process.HasExited) throw new InvalidOperationException("Sample exited before completing onboarding.");
                    try { using var response = await client.GetAsync("/", token); return response.IsSuccessStatusCode; }
                    catch (HttpRequestException) { return false; }
                    catch (TaskCanceledException) when (!token.IsCancellationRequested) { return false; }
                }
                await WaitAsync(SendRequest, token);
                await WaitAsync(async () => (await discovery.ListAsync(1, token)).Any(x => x.SourceId == source.SourceId && x.ServiceKey == "orders"), token);
                var dbFactory = services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
                await using (var before = await dbFactory.CreateDbContextAsync(token))
                {
                    Assert.AreEqual(0, await before.LogRecords.CountAsync(token));
                    Assert.AreEqual(0, await before.MetricPoints.CountAsync(token));
                    Assert.AreEqual(0, await before.TraceSpans.CountAsync(token));
                }
                var candidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == source.SourceId);
                var approved = await services.GetRequiredService<ServiceApproval>().ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
                Assert.AreEqual(ApprovalStatus.Approved, approved.Status);
                Assert.IsNotNull(approved.ServiceId);
                var serviceId = approved.ServiceId.Value;
                await WaitAsync(async () =>
                {
                    await SendRequest();
                    await using var db = await dbFactory.CreateDbContextAsync(token);
                    return await db.LogRecords.AnyAsync(x => x.ServiceId == serviceId, token)
                        && await db.TraceSpans.AnyAsync(x => x.ServiceId == serviceId, token)
                        && await db.MetricPoints.AnyAsync(x => x.ServiceId == serviceId, token);
                }, token);
                var details = await services.GetRequiredService<ServiceDetailsReader>().ReadAsync(serviceId, token);
                Assert.IsNotNull(details);
                Assert.IsNotNull(details.LastLogReceivedAt);
                Assert.IsNotNull(details.LastTraceReceivedAt);
                Assert.IsNotNull(details.LastMetricReceivedAt);
                var history = services.GetRequiredService<TelemetryHistory>();
                var from = DateTimeOffset.UtcNow.AddMinutes(-5); var to = DateTimeOffset.UtcNow.AddMinutes(1);
                Assert.IsNotEmpty((await history.LogsAsync(serviceId, from, to, 1, null, token)).Items);
                Assert.IsNotEmpty((await history.TracesAsync(serviceId, from, to, 1, null, token)).Items);
                var actor = Guid.CreateVersion7();
                var policy = await services.GetRequiredService<HealthNetworkPolicyAdministration>().UpdateAsync(new(["127.0.0.0/8"], ""), actor, token);
                Assert.IsNotNull(policy);
                var target = await services.GetRequiredService<HealthTargetAdministration>().CreateAsync(new(serviceId, null,
                    new($"http://127.0.0.1:{samplePort}/health", 5, 5, 1, 1, true, "")), actor, token);
                Assert.AreEqual(HealthTargetStatus.Saved, target.Status);
                Assert.IsNotNull(target.Target);
                using var runner = new HealthRunner(dbFactory, TimeProvider.System);
                await runner.RunOnceAsync(token);
                var health = await services.GetRequiredService<HealthStatusReader>().ReadAsync(serviceId, token);
                Assert.IsNotNull(health);
                Assert.AreEqual(ServiceHealthState.Healthy, health.State);
                using var publicOriginal = PingTestSite.CreateFactory(database.ConnectionString);
                using var publicHost = publicOriginal.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<PublicStreamAdmission>();
                    services.AddSingleton(new PublicStreamAdmission(64, TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(5), TimeProvider.System));
                }));
                publicHost.UseKestrel(0); publicHost.StartServer();
                async Task RecheckHealthAsync(CancellationToken cancellationToken)
                {
                    await WaitAsync(async () =>
                    {
                        await runner.RunOnceAsync(cancellationToken);
                        var current = await services.GetRequiredService<HealthStatusReader>().ReadAsync(serviceId, cancellationToken);
                        return current?.State == ServiceHealthState.Healthy;
                    }, cancellationToken);
                }
                await SamplePublicationAcceptance.VerifyAsync(address, publicHost.ClientOptions.BaseAddress, certificate, serviceId,
                    source.SourceId, source.Secret, samplePort, ct => services.GetRequiredService<HealthStatusReader>().ReadAsync(serviceId, ct), RecheckHealthAsync, token);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                List<Exception> cleanupFailures = [];
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) { cleanupFailures.Add(error); }
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(cleanupDeadline.Token); }
                catch (Exception error) { cleanupFailures.Add(error); }
                try { await Task.WhenAll(stdout, stderr).WaitAsync(cleanupDeadline.Token); }
                catch (Exception error) { cleanupFailures.Add(error); }
                if (cleanupFailures.Count > 0)
                {
                    if (failure is not null) cleanupFailures.Insert(0, failure);
                    throw new AggregateException("Sample test or process cleanup failed.", cleanupFailures);
                }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { File.Delete(certificateFile); }
    }, context.CancellationToken);

    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (await condition()) return;
            await Task.Delay(250, token);
        }
        Assert.Fail("Timed out waiting for sample onboarding.");
    }
}
