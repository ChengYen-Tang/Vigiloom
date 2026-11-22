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
            var replacementUpdater = Required(prefix + "REPLACEMENT_UPDATER");
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
                var gate = new AgentTransportGate();
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
                var configured = await configuration.UpdateAsync(null, new(new(10, 60, 1), ""), actor, token);
                Assert.IsNotNull(configured);
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
                if (!windows) await VerifyLinuxInstallationPermissionsAsync(token);
                var installationRoot = windows
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VigiloomAgent", "Agent")
                    : "/opt/vigiloom/agent";
                var updaterState = windows
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VigiloomAgent", "Updater")
                    : "/var/lib/vigiloom/updater";
                var abandonedPreparation = Path.Combine(installationRoot, "staging", Guid.CreateVersion7().ToString("N"));
                var abandonedCache = Path.Combine(updaterState, "cache");
                var partialDownload = Path.Combine(abandonedCache, "interrupted.download");
                var retainedPackage = Path.Combine(abandonedCache, "retained-test.pkg");
                if (windows) Directory.CreateDirectory(abandonedPreparation);
                else await RunAsync("sudo", token, "-n", "mkdir", "-p", abandonedPreparation);
                if (windows)
                {
                    Directory.CreateDirectory(abandonedCache);
                    await File.WriteAllTextAsync(partialDownload, "partial", token);
                    await File.WriteAllTextAsync(retainedPackage, "retained", token);
                }
                else await RunAsync("sudo", token, "-n", "sh", "-c", "mkdir -p \"$1\"; printf partial > \"$2\"; printf retained > \"$3\"",
                    "sh", abandonedCache, partialDownload, retainedPackage);
                Assert.IsTrue(Directory.Exists(abandonedPreparation));
                if (windows) await RunAsync("powershell.exe", token, "-NoProfile", "-Command", "Restart-Service -Name 'Vigiloom Updater'");
                else await RunAsync("sudo", token, "-n", "systemctl", "restart", "vigiloom-updater.service");
                await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(abandonedPreparation)), token);
                await WaitUntilAsync(async () => !await HasPartialDownloadAsync(windows, abandonedCache, token, requireData: false), token);
                if (windows)
                {
                    Assert.AreEqual("retained", await File.ReadAllTextAsync(retainedPackage, token));
                    File.Delete(retainedPackage);
                }
                else await RunAsync("sudo", token, "-n", "sh", "-c", "printf retained | cmp -s \"$1\" - && rm -- \"$1\"", "sh", retainedPackage);
                var logPath = windows
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VigiloomAgent", "Agent", "cursor-test.log")
                    : "/var/lib/vigiloom/agent/cursor-test.log";
                await AppendLogAsync(windows, logPath, "before-update", token);
                var logSource = await scope.ServiceProvider.GetRequiredService<HostLogConfiguration>().CreateAsync(device.Id,
                    new(AgentLogSourceKind.TextFile, "update-cursor-test", logPath, true, AgentTextLogMode.SingleLine), actor, token);
                Assert.AreEqual(HostLogConfigurationStatus.Success, logSource.Status);
                Assert.IsNotNull(logSource.Source);
                // Enrollment can finish after the settings worker enters its normal five-minute polling delay.
                // Restarting the installed service starts the next synchronization without extending the test by minutes.
                await SetServiceRunningAsync(windows, running: false, token);
                await SetServiceRunningAsync(windows, running: true, token);
                DateTimeOffset? appliedAt = null;
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    var report = await db.Agents.AsNoTracking().SingleAsync(x => x.Id == agentId, token);
                    if (report.AppliedConfigurationRevision != configured.Effective.Revision || report.ConfigurationAppliedAt is null) return false;
                    appliedAt = report.ConfigurationAppliedAt;
                    return true;
                }, token);
                gate.BlockSettings();
                var logMonitoring = scope.ServiceProvider.GetRequiredService<HostLogMonitoring>();
                await WaitForLogEndAsync(logMonitoring, agents, agentId, device.Id, logSource.Source.Settings, DateTimeOffset.UtcNow, token);
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
                await WaitForResultAsync(agents, agentId, current.Value, AgentUpdateResultKind.Failed, token, TimeSpan.FromMinutes(7));
                var failed = windows
                    ? await new UpdateJournal(updaterState).ReadAsync(token)
                    : System.Text.Json.JsonSerializer.Deserialize<UpdateState>(await RunAsync("sudo", token, "-n", "python3", "-c",
                        "import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute('SELECT State FROM UpdateJournal WHERE Id=1').fetchone()[0])",
                        Path.Combine(updaterState, "update.db")));
                Assert.IsNotNull(failed);
                Assert.AreEqual(UpdatePhase.Failed, failed.Phase);
                Assert.IsTrue(Directory.Exists(failed.TargetDirectory));
                Assert.AreEqual(updaterPid, await ServicePidAsync(windows, agent: false, token));
                await AssignAndWaitAsync(agents, assignments, agentId, "1.1.0", AgentUpdateResultKind.Succeeded, actor, token, TimeSpan.FromMinutes(7));
                await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(failed.TargetDirectory)), token);
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.Agents.AnyAsync(x => x.Id == agentId && x.RuntimeVersion == "1.1.0", token);
                }, token);
                await AppendLogAsync(windows, logPath, "after-upgrade", token);
                await WaitForLogEndAsync(logMonitoring, agents, agentId, device.Id, logSource.Source.Settings, DateTimeOffset.UtcNow, token);
                var successfulUpdate = await ReadUpdateStateAsync(windows, updaterState, token);
                Assert.IsNotNull(successfulUpdate);
                Assert.AreEqual(UpdatePhase.Succeeded, successfulUpdate.Phase);
                IAgentUpdateRuntime agentRuntime = OperatingSystem.IsWindows()
                    ? new WindowsAgentRuntime(installationRoot) : OperatingSystem.IsLinux()
                        ? new SystemdAgentRuntime(installationRoot) : throw new PlatformNotSupportedException();
                for (var crash = 0; crash < 2; crash++)
                {
                    var agentPid = await ServicePidAsync(windows, agent: true, token);
                    Assert.AreNotEqual("0", agentPid);
                    if (windows) await RunAsync("taskkill.exe", token, "/PID", agentPid, "/F");
                    else await RunAsync("sudo", token, "-n", "kill", "-KILL", agentPid);
                    await WaitUntilAsync(async () => await ServicePidAsync(windows, agent: true, token) != agentPid, token);
                    var afterCrash = await ReadUpdateStateAsync(windows, updaterState, token);
                    Assert.IsNotNull(afterCrash);
                    Assert.AreEqual(successfulUpdate.Id, afterCrash.Id);
                    Assert.AreEqual(UpdatePhase.Succeeded, afterCrash.Phase);
                    Assert.IsTrue(Directory.Exists(afterCrash.TargetDirectory));
                    Assert.AreEqual(Path.GetFullPath(afterCrash.TargetDirectory), await agentRuntime.GetSelectedDirectoryAsync(token));
                    Assert.AreEqual(updaterPid, await ServicePidAsync(windows, agent: false, token));
                    await SetServiceRunningAsync(windows, running: true, token);
                }
                await AssignAndWaitAsync(agents, assignments, agentId, "1.0.0", AgentUpdateResultKind.Succeeded, actor, token, TimeSpan.FromMinutes(7));
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.Agents.AnyAsync(x => x.Id == agentId && x.RuntimeVersion == "1.0.0", token);
                }, token);
                await AppendLogAsync(windows, logPath, "after-downgrade", token);
                await WaitForLogEndAsync(logMonitoring, agents, agentId, device.Id, logSource.Source.Settings, DateTimeOffset.UtcNow, token);
                Assert.AreEqual(updaterPid, await ServicePidAsync(windows, agent: false, token),
                    "An Agent rollback or downgrade must not replace the Updater.");
                // Pause the post-stop authorization check so the real Updater can be interrupted
                // at a known durable phase without a product-only test hook.
                gate.PauseAssignmentWhen(async ct => await ServicePidAsync(windows, agent: true, ct) == "0");
                var interruptedId = await SetAssignmentAsync(agents, assignments, agentId, "1.1.0", actor, token);
                Assert.IsNotNull(interruptedId);
                installed.TrackReplacement(interruptedId.Value);
                string killedPid;
                string interruptedDirectory;
                try
                {
                    await gate.AssignmentPaused.WaitAsync(TimeSpan.FromMinutes(7), token);
                    var interruptedState = await ReadUpdateStateAsync(windows, updaterState, token);
                    Assert.IsNotNull(interruptedState);
                    Assert.AreEqual(interruptedId.Value, interruptedState.Id);
                    Assert.AreEqual(UpdatePhase.Stopping, interruptedState.Phase);
                    Assert.IsTrue(Directory.Exists(interruptedState.TargetDirectory));
                    interruptedDirectory = interruptedState.TargetDirectory;
                    killedPid = await ServicePidAsync(windows, agent: false, token);
                    Assert.AreNotEqual("0", killedPid);
                    if (windows) await RunAsync("taskkill.exe", token, "/PID", killedPid, "/F");
                    else await RunAsync("sudo", token, "-n", "kill", "-KILL", killedPid);
                }
                finally { gate.ReleaseAssignment(); }
                if (windows)
                {
                    await WaitUntilAsync(async () => await ServicePidAsync(windows, agent: false, token) != killedPid, token);
                    if (await ServicePidAsync(windows, agent: false, token) == "0")
                        await RunAsync("powershell.exe", token, "-NoProfile", "-Command", "Start-Service -Name 'Vigiloom Updater'");
                }
                else await RunAsync("sudo", token, "-n", "systemctl", "restart", "vigiloom-updater.service");
                await WaitUntilAsync(async () => (await ReadUpdateStateAsync(windows, updaterState, token)) is
                    { Phase: UpdatePhase.Failed, Error: "Interrupted" }, token);
                Assert.IsTrue(Directory.Exists(interruptedDirectory));
                Assert.AreEqual("0", await ServicePidAsync(windows, agent: true, token));
                await AssignAndWaitAsync(agents, assignments, agentId, "1.0.0", AgentUpdateResultKind.Succeeded, actor, token, TimeSpan.FromMinutes(7));
                updaterPid = await ServicePidAsync(windows, agent: false, token);
                Assert.AreNotEqual("0", updaterPid);
                Assert.AreNotEqual(killedPid, updaterPid);
                await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(interruptedDirectory)), token);

                // Interrupt the real Updater after a partial archive is written; the same
                // assignment should be retried and the incomplete download removed.
                var cacheDirectory = Path.Combine(updaterState, "cache");
                var runningPid = await ServicePidAsync(windows, agent: true, token);
                gate.PausePackage();
                var downloadId = await SetAssignmentAsync(agents, assignments, agentId, "1.2.0", actor, token);
                Assert.IsNotNull(downloadId);
                installed.TrackReplacement(downloadId.Value);
                try
                {
                    await gate.PackagePaused.WaitAsync(TimeSpan.FromMinutes(7), token);
                    await WaitUntilAsync(() => HasPartialDownloadAsync(windows, cacheDirectory, token), token);
                    Assert.AreEqual(runningPid, await ServicePidAsync(windows, agent: true, token));
                    killedPid = updaterPid;
                    if (windows) await RunAsync("taskkill.exe", token, "/PID", killedPid, "/F");
                    else await RunAsync("sudo", token, "-n", "kill", "-KILL", killedPid);
                }
                finally { gate.ReleasePackage(); }
                if (windows)
                {
                    await WaitUntilAsync(async () => await ServicePidAsync(windows, agent: false, token) != killedPid, token);
                    if (await ServicePidAsync(windows, agent: false, token) == "0")
                        await RunAsync("powershell.exe", token, "-NoProfile", "-Command", "Start-Service -Name 'Vigiloom Updater'");
                }
                else await RunAsync("sudo", token, "-n", "systemctl", "restart", "vigiloom-updater.service");
                // Version 1.2.0 is deliberately broken, so the retry completes as an
                // Agent startup failure; it must not leave a partial archive behind.
                await WaitForResultAsync(agents, agentId, downloadId.Value, AgentUpdateResultKind.Failed, token, TimeSpan.FromMinutes(7));
                UpdateState? failedRetry = null;
                await WaitUntilAsync(async () =>
                {
                    failedRetry = await ReadUpdateStateAsync(windows, updaterState, token);
                    return failedRetry is { Phase: UpdatePhase.Failed, RetryAllowed: false } && failedRetry.Id == downloadId;
                }, token, TimeSpan.FromMinutes(7));
                Assert.IsNotNull(failedRetry);
                Assert.IsTrue(Directory.Exists(failedRetry.TargetDirectory));
                Assert.AreEqual(Path.GetFullPath(failedRetry.TargetDirectory), await agentRuntime.GetSelectedDirectoryAsync(token));
                Assert.IsFalse(await HasPartialDownloadAsync(windows, cacheDirectory, token, requireData: false));
                await AssignAndWaitAsync(agents, assignments, agentId, "1.0.0", AgentUpdateResultKind.Succeeded, actor, token, TimeSpan.FromMinutes(7));
                updaterPid = await ServicePidAsync(windows, agent: false, token);

                // The real service stays in health verification for at least ten seconds,
                // giving us a durable phase to interrupt without adding hooks to the product.
                var verifyingId = await SetAssignmentAsync(agents, assignments, agentId, "1.1.0", actor, token);
                Assert.IsNotNull(verifyingId);
                installed.TrackReplacement(verifyingId.Value);
                killedPid = updaterPid;
                UpdateState? verifying = null;
                await WaitUntilAsync(async () =>
                {
                    verifying = await ReadUpdateStateAsync(windows, updaterState, token);
                    return verifying is { Phase: UpdatePhase.Verifying } && verifying.Id == verifyingId;
                }, token, TimeSpan.FromMinutes(7));
                Assert.IsNotNull(verifying);
                Assert.IsTrue(Directory.Exists(verifying.TargetDirectory));
                Assert.AreNotEqual("0", killedPid);
                if (windows) await RunAsync("taskkill.exe", token, "/PID", killedPid, "/F");
                else await RunAsync("sudo", token, "-n", "kill", "-KILL", killedPid);
                if (windows)
                {
                    await WaitUntilAsync(async () => await ServicePidAsync(windows, agent: false, token) != killedPid, token);
                    if (await ServicePidAsync(windows, agent: false, token) == "0")
                        await RunAsync("powershell.exe", token, "-NoProfile", "-Command", "Start-Service -Name 'Vigiloom Updater'");
                }
                else await RunAsync("sudo", token, "-n", "systemctl", "restart", "vigiloom-updater.service");
                await WaitUntilAsync(async () => (await ReadUpdateStateAsync(windows, updaterState, token)) is
                    { Phase: UpdatePhase.Failed, Error: "Interrupted" } state && state.Id == verifyingId, token);
                Assert.IsTrue(Directory.Exists(verifying.TargetDirectory));
                Assert.AreEqual(Path.GetFullPath(verifying.TargetDirectory), await agentRuntime.GetSelectedDirectoryAsync(token));
                await AssignAndWaitAsync(agents, assignments, agentId, "1.0.0", AgentUpdateResultKind.Succeeded, actor, token, TimeSpan.FromMinutes(7));
                await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(verifying.TargetDirectory)), token);
                updaterPid = await ServicePidAsync(windows, agent: false, token);
                Assert.AreNotEqual("0", updaterPid);
                Assert.AreNotEqual(killedPid, updaterPid);

                var installedUpdater = Path.Combine(windows
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VigiloomAgent", "Updater")
                    : "/opt/vigiloom/updater", "Vigiloom.Updater.dll");
                var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(updater, "Vigiloom.Updater.dll"), token));
                var replacementHash = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(replacementUpdater, "Vigiloom.Updater.dll"), token));
                Assert.IsFalse(originalHash.AsSpan().SequenceEqual(replacementHash), "The test requires different Updater builds.");
                var upgradeId = await SetAssignmentAsync(agents, assignments, agentId, "1.1.0", actor, token);
                Assert.IsNotNull(upgradeId);
                installed.TrackReplacement(upgradeId.Value);
                await WaitForResultAsync(agents, agentId, upgradeId.Value, AgentUpdateResultKind.Succeeded, token, TimeSpan.FromMinutes(7));
                await WaitUntilAsync(async () =>
                {
                    if (!File.Exists(installedUpdater)) return false;
                    try
                    {
                        var statusPath = Path.Combine(updaterState, "updater-replacement.status");
                        var status = windows
                            ? File.Exists(statusPath) ? await File.ReadAllTextAsync(statusPath, token) : ""
                            : await RunAsync("sudo", token, "-n", "sh", "-c", "test ! -f /var/lib/vigiloom/updater/updater-replacement.status || cat /var/lib/vigiloom/updater/updater-replacement.status");
                        if (status.Trim() != $"{upgradeId.Value:N} succeeded") return false;
                        var baseDirectory = Path.GetDirectoryName(Path.GetDirectoryName(installedUpdater))!;
                        if (Directory.Exists(Path.Combine(baseDirectory, "Updater.previous")) || Directory.Exists(Path.Combine(baseDirectory, "Updater.next")) ||
                            Directory.Exists(Path.Combine(baseDirectory, "updater.previous")) || Directory.Exists(Path.Combine(baseDirectory, "updater.next"))) return false;
                        var hash = SHA256.HashData(await File.ReadAllBytesAsync(installedUpdater, token));
                        var pid = await ServicePidAsync(windows, agent: false, token);
                        return hash.AsSpan().SequenceEqual(replacementHash) && pid != "0" && pid != updaterPid;
                    }
                    catch (IOException) { return false; }
                }, token, TimeSpan.FromMinutes(4));
                var upgradedUpdaterPid = await ServicePidAsync(windows, agent: false, token);
                var reportAfterUpdate = DateTimeOffset.UtcNow;
                var previousRejections = gate.SettingsRejections;
                await WaitUntilAsync(() => Task.FromResult(gate.SettingsRejections > previousRejections), token, TimeSpan.FromMinutes(3));
                string? lastObservation = null;
                try
                {
                    await WaitUntilAsync(async () =>
                    {
                        await using var db = await agents.CreateDbContextAsync(token);
                        var row = await db.Agents.AsNoTracking().Where(x => x.Id == agentId).Select(x => new
                        {
                            x.LastHeartbeatAt, x.RuntimeVersion, x.RuntimeReportedAt, x.ConfigurationReportedAt,
                            x.AppliedConfigurationRevision, x.ConfigurationAppliedAt, x.ConfigurationFailure
                        }).SingleOrDefaultAsync(token);
                        if (row is null) { lastObservation = "Agent missing"; return false; }
                        lastObservation = $"heartbeat={row.LastHeartbeatAt:O}; runtime={row.RuntimeVersion}; runtimeAt={row.RuntimeReportedAt:O}; " +
                            $"configAt={row.ConfigurationReportedAt:O}; revisionMatches={row.AppliedConfigurationRevision == configured.Effective.Revision}; " +
                            $"appliedAtMatches={row.ConfigurationAppliedAt == appliedAt}; failure={row.ConfigurationFailure}; rejections={gate.SettingsRejections}";
                        return row.ConfigurationReportedAt > reportAfterUpdate && row.AppliedConfigurationRevision == configured.Effective.Revision &&
                            row.ConfigurationAppliedAt == appliedAt && row.ConfigurationFailure == AgentConfigurationFailure.Unavailable;
                    }, token);
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException("Agent configuration observation did not arrive: " + lastObservation, error);
                }
                Assert.AreEqual(upgradedUpdaterPid, await ServicePidAsync(windows, agent: false, token));
                Assert.AreNotEqual(originalAgentPid, await ServicePidAsync(windows, agent: true, token));
                if (!windows) await VerifyLinuxInstallationPermissionsAsync(token);
                await using (var db = await agents.CreateDbContextAsync(token))
                    Assert.IsFalse(await db.UpdateReceipts.AnyAsync(x => x.AgentId == agentId &&
                        (x.UpdateId == cancelled.Value || x.UpdateId == replaced.Value), token));

                var agentPidBeforeRepair = await ServicePidAsync(windows, agent: true, token);
                Assert.AreNotEqual("0", agentPidBeforeRepair);
                var replacementOutput = Path.Combine(path, "replacement-output");
                var replacementBundle = Path.Combine(path, "replacement-bundle");
                Directory.CreateDirectory(replacementBundle);
                if (windows)
                {
                    await RunAsync("powershell.exe", token, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        Path.Combine(workspace, "packaging/agent/windows/bundle.ps1"), "-AgentPublish", initial,
                        "-UpdaterPublish", replacementUpdater, "-TrustedRoot", rootPath, "-RootSha256", fingerprint,
                        "-OutputDirectory", replacementOutput);
                    ZipFile.ExtractToDirectory(Path.Combine(replacementOutput, $"vigiloom-agent-{rid}.zip"), replacementBundle);
                }
                else
                {
                    await RunAsync("bash", token, Path.Combine(workspace, "packaging/agent/linux/bundle.sh"), initial,
                        replacementUpdater, rootPath, fingerprint, replacementOutput);
                    await RunAsync("tar", token, "-xzf", Path.Combine(replacementOutput, "vigiloom-agent-linux-x64.tar.gz"), "-C", replacementBundle);
                }
                CollectionAssert.AreEqual(replacementHash, SHA256.HashData(await File.ReadAllBytesAsync(installedUpdater, token)));
                // Repair must work even when the installed Updater executable itself is missing.
                if (windows)
                    await RunAsync("powershell.exe", token, "-NoProfile", "-Command",
                        "$ErrorActionPreference='Stop'; Stop-Service -Name 'Vigiloom Updater'; " +
                        "Remove-Item -LiteralPath (Join-Path $env:ProgramFiles 'VigiloomAgent\\Updater\\Vigiloom.Updater.exe')");
                else
                {
                    await RunAsync("sudo", token, "-n", "systemctl", "stop", "vigiloom-updater.service");
                    await RunAsync("sudo", token, "-n", "rm", "--", "/opt/vigiloom/updater/Vigiloom.Updater");
                }
                if (windows)
                    await RunAsync("powershell.exe", token, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        Path.Combine(replacementBundle, "repair-updater.ps1"));
                else
                    await RunAsync("sudo", token, "-n", "bash", Path.Combine(replacementBundle, "repair-updater.sh"));
                var repairedUpdaterPid = await ServicePidAsync(windows, agent: false, token);
                Assert.AreNotEqual("0", repairedUpdaterPid);
                Assert.AreNotEqual(upgradedUpdaterPid, repairedUpdaterPid);
                Assert.AreEqual(agentPidBeforeRepair, await ServicePidAsync(windows, agent: true, token));
                CollectionAssert.AreEqual(replacementHash, SHA256.HashData(await File.ReadAllBytesAsync(installedUpdater, token)));

                if (windows)
                {
                    await RunAsync("powershell.exe", token, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(bundle, "remove.ps1"));
                    await WaitUntilAsync(async () => await RunAsync("powershell.exe", token, "-NoProfile", "-Command",
                        "@(Get-Service -Name 'Vigiloom Agent','Vigiloom Updater' -ErrorAction SilentlyContinue).Count") == "0", token);
                }
                else
                {
                    await RunAsync("sudo", token, "-n", "bash", Path.Combine(bundle, "remove.sh"));
                    Assert.IsFalse(File.Exists("/etc/systemd/system/vigiloom-agent.service"));
                    Assert.IsFalse(File.Exists("/etc/systemd/system/vigiloom-updater.service"));
                }
                var removedAt = DateTimeOffset.UtcNow;
                if (windows)
                    await RunAsync("powershell.exe", token, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(bundle, "restore.ps1"));
                else
                    await RunAsync("sudo", token, "-n", "bash", Path.Combine(bundle, "restore.sh"));
                Assert.AreNotEqual("0", await ServicePidAsync(windows, agent: true, token));
                Assert.AreNotEqual("0", await ServicePidAsync(windows, agent: false, token));
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.Agents.AnyAsync(x => x.Id == agentId && x.LastHeartbeatAt > removedAt && x.RuntimeVersion == "1.1.0", token);
                }, token);

                gate.Allow();
                var replay = await gate.Retried.WaitAsync(TimeSpan.FromMinutes(3), token);
                CollectionAssert.AreEqual(blocked, replay);
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.ReceivedBatches.CountAsync(x => x.AgentId == agentId && x.BatchId == original.BatchId, token) == 1;
                }, token);
                await WaitUntilAsync(async () =>
                {
                    await using var db = await agents.CreateDbContextAsync(token);
                    return await db.HostLogs.CountAsync(x => x.AgentId == agentId && x.SourceId == logSource.Source.Settings.SourceId, token) >= 3;
                }, token);
                await using (var db = await agents.CreateDbContextAsync(token))
                {
                    var records = await db.HostLogs.AsNoTracking().Where(x => x.AgentId == agentId && x.SourceId == logSource.Source.Settings.SourceId)
                        .OrderBy(x => x.RowId).ToArrayAsync(token);
                    CollectionAssert.AreEqual(new[] { "before-update\n", "after-upgrade\n", "after-downgrade\n" }, records.Select(x => x.Content).ToArray());
                    Assert.IsTrue(records.All(x => x.GapReason is null));
                }
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

    private static async Task<UpdateState?> ReadUpdateStateAsync(bool windows, string directory, CancellationToken token) => windows
        ? await new UpdateJournal(directory).ReadAsync(token)
        : System.Text.Json.JsonSerializer.Deserialize<UpdateState>(await RunAsync("sudo", token, "-n", "python3", "-c",
            "import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute('SELECT State FROM UpdateJournal WHERE Id=1').fetchone()[0])",
            Path.Combine(directory, "update.db")));

    private static async Task AppendLogAsync(bool windows, string path, string line, CancellationToken token)
    {
        if (windows) await File.AppendAllTextAsync(path, line + "\n", token);
        else await RunAsync("sudo", token, "-n", "sh", "-c", "printf '%s\\n' \"$2\" >> \"$1\" && chmod 0644 \"$1\"", "sh", path, line);
    }

    private static async Task WaitForLogEndAsync(HostLogMonitoring monitoring, IDbContextFactory<AgentManagementDbContext> agents,
        Guid agentId, Guid deviceId, AgentLogSourceSetting source,
        DateTimeOffset after, CancellationToken token)
    {
        string observation = "no report";
        try
        {
            await WaitUntilAsync(async () =>
            {
                var status = await monitoring.GetAsync(deviceId, token);
                var match = status?.Report.Sources.FirstOrDefault(x => x.SourceId == source.SourceId);
                observation = $"throttled={status?.Report.Throttled}; state={match?.State}; generationMatches={match?.Generation == source.Generation}; observedAt={match?.ObservedAt:O}; ageSeconds={match?.AgeSeconds}; requiredAfter={after:O}";
                return status?.Report.Throttled == false && match is not null && match.Generation == source.Generation &&
                    match.State == AgentLogSourceState.End && match.ObservedAt >= after;
            }, token);
        }
        catch (TimeoutException error)
        {
            await using var db = await agents.CreateDbContextAsync(token);
            var row = await db.Agents.AsNoTracking().Where(x => x.Id == agentId).Select(x => new
            {
                x.LastHeartbeatAt, x.RuntimeVersion, x.RuntimeReportedAt, x.QueueReportEpoch, x.QueueReportSequence,
                x.LogStatusReportEpoch, x.LogStatusReportSequence, x.LogStatusReportedAt
            }).SingleOrDefaultAsync(token);
            throw new TimeoutException($"Agent log source did not reach end: {observation}; heartbeat={row?.LastHeartbeatAt:O}; " +
                $"runtime={row?.RuntimeVersion}; runtimeAt={row?.RuntimeReportedAt:O}; " +
                $"epochsMatch={row?.QueueReportEpoch == row?.LogStatusReportEpoch}; " +
                $"sequencesMatch={row?.QueueReportSequence == row?.LogStatusReportSequence}; logReportedAt={row?.LogStatusReportedAt:O}", error);
        }
    }

    private static Task<string> VerifyLinuxInstallationPermissionsAsync(CancellationToken token) =>
        RunAsync("sudo", token, "-n", "-u", "nobody", "--", "/bin/sh", "-c",
            "test -r \"$1\" && test -x \"$1\" && test ! -w \"$1\" && test -d \"$2\" && test ! -w \"$2\" && test -d \"$3\" && test ! -w \"$3\"",
            "sh", "/opt/vigiloom/agent/current/Vigiloom.Agent", "/opt/vigiloom/agent/current", "/opt/vigiloom/agent");

    private static async Task<bool> HasPartialDownloadAsync(bool windows, string directory, CancellationToken token, bool requireData = true) => windows
        ? Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.download").Any(path => !requireData || new FileInfo(path).Length > 0)
        : (await RunAsync("sudo", token, "-n", "sh", "-c", requireData
            ? "find \"$1\" -maxdepth 1 -name '*.download' -size +0c -print -quit"
            : "find \"$1\" -maxdepth 1 -name '*.download' -print -quit", "sh", directory)).Length > 0;

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
