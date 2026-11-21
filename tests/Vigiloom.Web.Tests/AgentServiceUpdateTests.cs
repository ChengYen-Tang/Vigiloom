using System.Diagnostics;
using System.IO.Compression;
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
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;
using Vigiloom.Updater.Core.Updates;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class AgentServiceUpdateTests(TestContext context)
{
    [TestMethod]
    [Timeout(1800000, CooperativeCancellation = true)]
    public Task Linux_RetainsBatchAcrossRollbackUpgradeAndDowngrade() => RunScenarioAsync(windows: false);

    [TestMethod]
    [Timeout(1800000, CooperativeCancellation = true)]
    public Task Windows_RetainsBatchAcrossRollbackUpgradeAndDowngrade() => RunScenarioAsync(windows: true);

    private Task RunScenarioAsync(bool windows)
    {
        if (Environment.GetEnvironmentVariable(windows ? "VIGILOOM_WINDOWS_AGENT_TEST" : "VIGILOOM_SYSTEMD_AGENT_TEST") != "1")
            Assert.Inconclusive("This test runs only in its dedicated disposable service runner.");
        if ((windows ? !OperatingSystem.IsWindows() : !OperatingSystem.IsLinux()) || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The service test requires its dedicated GitHub-hosted runner.");

        return SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var workspace = Required("GITHUB_WORKSPACE");
            var prefix = windows ? "VIGILOOM_WINDOWS_" : "VIGILOOM_SYSTEMD_";
            var initial = Required(prefix + "INITIAL_AGENT");
            var updater = Required(prefix + "UPDATER");
            var archivePath = Required(prefix + "UPDATE_ARCHIVE");
            var initialArchivePath = Required(prefix + "INITIAL_ARCHIVE");
            var rid = windows ? Required("VIGILOOM_WINDOWS_RID") : "linux-x64";
            if (windows && rid is not ("win-x64" or "win-x86"))
                throw new InvalidOperationException("The Windows service test requires a supported runtime identifier.");
            var path = Path.Combine(Required("RUNNER_TEMP"), $"vigiloom-service-{Guid.CreateVersion7():N}");
            Directory.CreateDirectory(path);
            try
            {
                using var packages = new SignedAgentPackages(await File.ReadAllBytesAsync(initialArchivePath, token),
                    await File.ReadAllBytesAsync(archivePath, token), rid);
                var rootPath = Path.Combine(path, "publisher-root.json");
                await File.WriteAllBytesAsync(rootPath, packages.Root, token);
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(packages.Root));
                var output = Path.Combine(path, "output");
                var bundle = Path.Combine(path, "bundle");
                Directory.CreateDirectory(bundle);
                if (windows)
                {
                    await RunAsync("powershell.exe", token, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        Path.Combine(workspace, "packaging/agent/windows/bundle.ps1"), "-AgentPublish", initial, "-UpdaterPublish", updater,
                        "-TrustedRoot", rootPath, "-RootSha256", fingerprint, "-OutputDirectory", output);
                    ZipFile.ExtractToDirectory(Path.Combine(output, $"vigiloom-agent-{rid}.zip"), bundle);
                    await VerifyWindowsRuntimePreflightAsync(bundle, token);
                }
                else
                {
                    await RunAsync("bash", token, Path.Combine(workspace, "packaging/agent/linux/bundle.sh"), initial, updater, rootPath, fingerprint, output);
                    await RunAsync("tar", token, "-xzf", Path.Combine(output, "vigiloom-agent-linux-x64.tar.gz"), "-C", bundle);
                }

                using var caKey = RSA.Create(2048);
                var caRequest = new CertificateRequest("CN=Vigiloom Systemd Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                var caPath = Path.Combine(path, "ca.pem");
                await File.WriteAllTextAsync(caPath, ca.ExportCertificatePem(), token);
                var caDerPath = Path.Combine(path, "ca.cer");
                await File.WriteAllBytesAsync(caDerPath, ca.Export(X509ContentType.Cert), token);
                using var serverKey = RSA.Create(2048);
                var serverRequest = new CertificateRequest("CN=Vigiloom Systemd Test", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
                serverRequest.CertificateExtensions.Add(san.Build());
                serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                using var unsigned = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
                using var generatedCertificate = unsigned.CopyWithPrivateKey(serverKey);
                var pfx = generatedCertificate.Export(X509ContentType.Pfx);
                using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
                CryptographicOperations.ZeroMemory(pfx);
                var gate = new AgentBatchGate();
                using var publisher = packages.CreateClient();
                var transport = new PackageLibraryTransport(Path.Combine(path, "cache"), 2L * 1024 * 1024 * 1024, TimeProvider.System, publisher);
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                        ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
                    }));
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
                await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
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

                var script = Path.Combine(workspace, windows ? "tests/Vigiloom.Web.Tests/Fixtures/windows-service-agent.ps1" : "tests/Vigiloom.Web.Tests/Fixtures/linux-systemd-agent.sh");
                await using var installed = windows
                    ? await ServiceAgentProcess.InstallWindowsAsync(script, bundle, caDerPath, server, device.Id, fingerprint, issued.Secret!, token)
                    : await ServiceAgentProcess.InstallLinuxAsync(script, bundle, caPath, server, device.Id, fingerprint, issued.Secret!, token);
                var agentId = await WaitForAgentAsync(agents, device.Id, rid, token);
                var blocked = await gate.Blocked.WaitAsync(TimeSpan.FromMinutes(3), token);
                Assert.IsTrue(AgentMetricBatchCodec.TryRead(blocked, out var original));
                Assert.IsNotNull(original);
                var updaterPid = await ServicePidAsync(windows, agent: false, token);
                var originalAgentPid = await ServicePidAsync(windows, agent: true, token);
                Assert.AreNotEqual("0", updaterPid);
                Assert.AreNotEqual("0", originalAgentPid);

                var assignments = scope.ServiceProvider.GetRequiredService<AgentVersionAssignments>();
                await SetServiceRunningAsync(windows, running: false, token);
                Assert.AreEqual(updaterPid, await ServicePidAsync(windows, agent: false, token));
                var cancelled = await SetAssignmentAsync(agents, assignments, agentId, "1.0.0", actor, token);
                Assert.IsNotNull(cancelled);
                Assert.IsNull(await SetAssignmentAsync(agents, assignments, agentId, null, actor, token));
                var replaced = await SetAssignmentAsync(agents, assignments, agentId, "1.1.0", actor, token);
                Assert.IsNotNull(replaced);
                var current = await SetAssignmentAsync(agents, assignments, agentId, "1.2.0", actor, token);
                Assert.IsNotNull(current);
                Assert.AreNotEqual(cancelled, replaced);
                Assert.AreNotEqual(replaced, current);
                await SetServiceRunningAsync(windows, running: true, token);
                await WaitForResultAsync(agents, agentId, current.Value, AgentUpdateResultKind.RolledBack, token, TimeSpan.FromMinutes(7));
                await AssignAndWaitAsync(agents, assignments, agentId, "1.1.0", AgentUpdateResultKind.Succeeded, actor, token, TimeSpan.FromMinutes(7));
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.Agents.AnyAsync(x => x.Id == agentId && x.RuntimeVersion == "1.1.0", token);
                }, token);
                await AssignAndWaitAsync(agents, assignments, agentId, "1.0.0", AgentUpdateResultKind.Succeeded, actor, token, TimeSpan.FromMinutes(7));
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.Agents.AnyAsync(x => x.Id == agentId && x.RuntimeVersion == "1.0.0", token);
                }, token);
                Assert.AreEqual(updaterPid, await ServicePidAsync(windows, agent: false, token));
                Assert.AreNotEqual(originalAgentPid, await ServicePidAsync(windows, agent: true, token));
                await using (var db = await agents.CreateDbContextAsync(token))
                    Assert.IsFalse(await db.UpdateReceipts.AnyAsync(x => x.AgentId == agentId &&
                        (x.UpdateId == cancelled.Value || x.UpdateId == replaced.Value), token));

                var agentPidBeforeRepair = await ServicePidAsync(windows, agent: true, token);
                Assert.AreNotEqual("0", agentPidBeforeRepair);
                if (windows)
                    await RunAsync("powershell.exe", token, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        Path.Combine(bundle, "repair-updater.ps1"));
                else
                    await RunAsync("sudo", token, "-n", "bash", Path.Combine(bundle, "repair-updater.sh"));
                var repairedUpdaterPid = await ServicePidAsync(windows, agent: false, token);
                Assert.AreNotEqual("0", repairedUpdaterPid);
                Assert.AreNotEqual(updaterPid, repairedUpdaterPid);
                Assert.AreEqual(agentPidBeforeRepair, await ServicePidAsync(windows, agent: true, token));

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

    private static async Task<Guid> WaitForAgentAsync(IDbContextFactory<AgentManagementDbContext> contexts, Guid deviceId, string rid, CancellationToken token)
    {
        Guid agentId = default;
        await WaitUntilAsync(async () =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.RevokedAt == null, token);
            if (agent?.LastHeartbeatAt is null || agent.RuntimeIdentifier != rid) return false;
            agentId = agent.Id; return true;
        }, token);
        return agentId;
    }

    private static Task<string> ServicePidAsync(bool windows, bool agent, CancellationToken token) => windows
        ? RunAsync("powershell.exe", token, "-NoProfile", "-Command", $"(Get-CimInstance Win32_Service -Filter \"Name='{(agent ? "Vigiloom Agent" : "Vigiloom Updater")}'\").ProcessId")
        : RunAsync("systemctl", token, "show", agent ? "vigiloom-agent.service" : "vigiloom-updater.service", "--property=MainPID", "--value");

    private static async Task VerifyWindowsRuntimePreflightAsync(string bundle, CancellationToken token)
    {
        foreach (var (directory, executable) in new[] { ("agent", "Vigiloom.Agent"), ("updater", "Vigiloom.Updater") })
        {
            var config = Path.Combine(bundle, "payload", directory, executable + ".runtimeconfig.json");
            var original = await File.ReadAllBytesAsync(config, token);
            try
            {
                await File.WriteAllTextAsync(config, "{", token);
                var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RunAsync("powershell.exe", token,
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(bundle, "install.ps1")));
                Assert.Contains("The bundled Agent or Updater could not start", error.Message);
            }
            finally { await File.WriteAllBytesAsync(config, original, CancellationToken.None); }
        }
    }

    private static async Task SetServiceRunningAsync(bool windows, bool running, CancellationToken token)
    {
        if (windows && OperatingSystem.IsWindows())
        {
            var runtime = new WindowsAgentRuntime(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VigiloomAgent", "Agent"));
            if (running) await runtime.StartAsync(token);
            else await runtime.StopAsync(token);
        }
        else
            await RunAsync("sudo", token, "-n", "systemctl", running ? "start" : "stop", "vigiloom-agent.service");
    }

    private static async Task AssignAndWaitAsync(IDbContextFactory<AgentManagementDbContext> contexts,
        AgentVersionAssignments assignments, Guid agentId, string version, AgentUpdateResultKind result, Guid actor, CancellationToken token,
        TimeSpan? timeout = null)
    {
        var updateId = await SetAssignmentAsync(contexts, assignments, agentId, version, actor, token);
        Assert.IsNotNull(updateId);
        await WaitForResultAsync(contexts, agentId, updateId.Value, result, token, timeout);
    }

    private static async Task<Guid?> SetAssignmentAsync(IDbContextFactory<AgentManagementDbContext> contexts,
        AgentVersionAssignments assignments, Guid agentId, string? version, Guid actor, CancellationToken token)
    {
        Guid? package = null;
        if (version is not null)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            package = await db.Packages.Where(x => x.Version == version).Select(x => x.Id).SingleAsync(token);
        }
        var current = await assignments.GetAsync(agentId, token);
        Assert.IsNotNull(current);
        Assert.AreEqual(VersionAssignmentResult.Saved, await assignments.SetAsync(agentId, package, current.Revision, actor, token));
        await using var verify = await contexts.CreateDbContextAsync(token);
        var assignment = await verify.VersionAssignments.AsNoTracking().SingleAsync(x => x.AgentId == agentId, token);
        Assert.AreEqual(package, assignment.PackageId);
        if (package is null) Assert.IsNull(assignment.UpdateId);
        else Assert.IsNotNull(assignment.UpdateId);
        return assignment.UpdateId;
    }

    private static async Task WaitForResultAsync(IDbContextFactory<AgentManagementDbContext> contexts,
        Guid agentId, Guid updateId, AgentUpdateResultKind result, CancellationToken token, TimeSpan? timeout)
    {
        await WaitUntilAsync(async () =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            return await db.UpdateReceipts.AnyAsync(x => x.AgentId == agentId && x.UpdateId == updateId && x.Result == result, token);
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
        if (OperatingSystem.IsWindows() && file.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)) WindowsPowerShell.Configure(start);
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
