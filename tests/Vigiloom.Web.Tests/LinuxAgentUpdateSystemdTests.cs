using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class LinuxAgentUpdateSystemdTests(TestContext context)
{
    [TestMethod]
    [Timeout(1200000, CooperativeCancellation = true)]
    public Task RegisteredAgent_RetainsBatchAcrossRollbackAndSignedUpdate()
    {
        if (Environment.GetEnvironmentVariable("VIGILOOM_SYSTEMD_AGENT_TEST") != "1")
            Assert.Inconclusive("This test runs only in the dedicated disposable systemd runner.");
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The systemd test requires a disposable GitHub-hosted Linux runner.");

        return SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var workspace = Required("GITHUB_WORKSPACE");
            var initial = Required("VIGILOOM_SYSTEMD_INITIAL_AGENT");
            var updater = Required("VIGILOOM_SYSTEMD_UPDATER");
            var archivePath = Required("VIGILOOM_SYSTEMD_UPDATE_ARCHIVE");
            var path = Path.Combine(Required("RUNNER_TEMP"), $"vigiloom-systemd-{Guid.CreateVersion7():N}");
            Directory.CreateDirectory(path);
            try
            {
                using var packages = new SignedLinuxPackages(await File.ReadAllBytesAsync(archivePath, token));
                var rootPath = Path.Combine(path, "publisher-root.json");
                await File.WriteAllBytesAsync(rootPath, packages.Root, token);
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(packages.Root));
                var output = Path.Combine(path, "output");
                var bundle = Path.Combine(path, "bundle");
                Directory.CreateDirectory(bundle);
                await RunAsync("bash", token, Path.Combine(workspace, "packaging/agent/linux/bundle.sh"), initial, updater, rootPath, fingerprint, output);
                await RunAsync("tar", token, "-xzf", Path.Combine(output, "vigiloom-agent-linux-x64.tar.gz"), "-C", bundle);

                using var caKey = RSA.Create(2048);
                var caRequest = new CertificateRequest("CN=Vigiloom Systemd Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                var caPath = Path.Combine(path, "ca.pem");
                await File.WriteAllTextAsync(caPath, ca.ExportCertificatePem(), token);
                using var serverKey = RSA.Create(2048);
                var serverRequest = new CertificateRequest("CN=Vigiloom Systemd Test", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
                serverRequest.CertificateExtensions.Add(san.Build());
                serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                using var unsigned = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
                using var certificate = unsigned.CopyWithPrivateKey(serverKey);
                var gate = new AgentBatchGate();
                using var publisher = packages.CreateClient();
                var transport = new PackageLibraryTransport(Path.Combine(path, "cache"), 2L * 1024 * 1024 * 1024, TimeProvider.System, publisher);
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }));
                    builder.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                    builder.ConfigureServices(services =>
                    {
                        services.AddSingleton<IStartupFilter>(gate);
                        services.RemoveAll<IPackageLibraryTransport>();
                        services.AddSingleton<IPackageLibraryTransport>(transport);
                    });
                });
                factory.UseKestrel(0); factory.StartServer();
                await PingTestSite.InitializeAsync(factory, token);
                var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
                var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
                await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                var device = new Device("systemd test", "127.0.0.1", true, DateTimeOffset.UtcNow);
                await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
                var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                var server = new Uri(addresses.Single(x => x.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) + "/");
                await using var scope = factory.Services.CreateAsyncScope();
                var actor = Guid.CreateVersion7();
                var configuration = scope.ServiceProvider.GetRequiredService<AgentConfiguration>();
                Assert.IsNotNull(await configuration.UpdateAsync(null, new(new(10, 60, 1), ""), actor, token));
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                var issued = await identity.IssueAsync(device.Id, actor, token);
                Assert.AreEqual(AgentIdentityStatus.Success, issued.Status);
                var source = await scope.ServiceProvider.GetRequiredService<AgentPackageSourceSettings>().UpdateAsync(new("https://publisher.test/", PackageCredentialAction.Clear, null, ""), actor, token);
                Assert.IsNotNull(source);
                var trust = scope.ServiceProvider.GetRequiredService<UpdateTrustStore>();
                await trust.ProvisionAsync(packages.Root, token);
                var origin = new Uri("https://publisher.test/");
                await trust.RefreshCatalogueAsync(new TrustedCatalogueReader(origin, origin, packages.Repository), TimeProvider.System, Convert.FromBase64String(source.Version), cancellationToken: token);

                var script = Path.Combine(workspace, "tests/Vigiloom.Web.Tests/Fixtures/linux-systemd-agent.sh");
                await using var installed = await SystemdAgentProcess.InstallAsync(script, bundle, caPath, server, device.Id, fingerprint, issued.Secret!, token);
                var agentId = await WaitForAgentAsync(agents, device.Id, token);
                var blocked = await gate.Blocked.WaitAsync(TimeSpan.FromMinutes(3), token);
                Assert.IsTrue(AgentMetricBatchCodec.TryRead(blocked, out var original));
                Assert.IsNotNull(original);
                var updaterPid = await RunAsync("systemctl", token, "show", "vigiloom-updater.service", "--property=MainPID", "--value");
                var originalAgentPid = await RunAsync("systemctl", token, "show", "vigiloom-agent.service", "--property=MainPID", "--value");
                Assert.AreNotEqual("0", updaterPid);
                Assert.AreNotEqual("0", originalAgentPid);

                var assignments = scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>();
                await AssignAndWaitAsync(agents, assignments, agentId, "1.2.0", AgentUpdateResultKind.RolledBack, actor, token, TimeSpan.FromMinutes(7));
                await AssignAndWaitAsync(agents, assignments, agentId, "1.1.0", AgentUpdateResultKind.Succeeded, actor, token);
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.Agents.AnyAsync(x => x.Id == agentId && x.RuntimeVersion == "1.1.0", token);
                }, token);
                Assert.AreEqual(updaterPid, await RunAsync("systemctl", token, "show", "vigiloom-updater.service", "--property=MainPID", "--value"));
                Assert.AreNotEqual(originalAgentPid, await RunAsync("systemctl", token, "show", "vigiloom-agent.service", "--property=MainPID", "--value"));
                gate.Allow();
                var replay = await gate.Retried.WaitAsync(TimeSpan.FromMinutes(3), token);
                CollectionAssert.AreEqual(blocked, replay);
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.ReceivedBatches.CountAsync(x => x.AgentId == agentId && x.BatchId == original.BatchId, token) == 1;
                }, token);
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        }, context.CancellationToken);
    }

    private static async Task<Guid> WaitForAgentAsync(IDbContextFactory<AgentManagementDbContext> contexts, Guid deviceId, CancellationToken token)
    {
        Guid agentId = default;
        await WaitUntilAsync(async () =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.RevokedAt == null, token);
            if (agent?.LastHeartbeatAt is null) return false;
            agentId = agent.Id; return true;
        }, token);
        return agentId;
    }

    private static async Task AssignAndWaitAsync(IDbContextFactory<AgentManagementDbContext> contexts,
        AgentVersionAssignments assignments, Guid agentId, string version, AgentUpdateResultKind result, Guid actor, CancellationToken token,
        TimeSpan? timeout = null)
    {
        Guid package;
        await using (var db = await contexts.CreateDbContextAsync(token)) package = await db.Packages.Where(x => x.Version == version).Select(x => x.Id).SingleAsync(token);
        var current = await assignments.GetAsync(agentId, token);
        Assert.IsNotNull(current);
        Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agentId, package, current.Revision, actor, token));
        await WaitUntilAsync(async () =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            return await (from receipt in db.UpdateReceipts join target in db.UpdateTargets on receipt.UpdateId equals target.Id
                where receipt.AgentId == agentId && target.PackageId == package && receipt.Result == result select receipt.RowId).AnyAsync(token);
        }, token, timeout);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> check, CancellationToken token, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < (timeout ?? TimeSpan.FromMinutes(4)))
        {
            if (await check()) return;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        throw new TimeoutException("The expected Agent integration event did not arrive.");
    }

    private static async Task<string> RunAsync(string file, CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var result = await output;
        var diagnostic = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {result} {diagnostic}");
        return result.Trim();
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"The test requires {name}.");
}
