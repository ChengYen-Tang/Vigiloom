extern alias AgentHarness;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentProcessReplayTests(TestContext context)
{
    [TestMethod]
    [Timeout(240000, CooperativeCancellation = true)]
    public Task ReopenedAgentHost_ReplaysACommittedBatchWithoutDuplicates() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Vigiloom Replay Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
            var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(x => x.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            var server = new UriBuilder(address) { Host = "localhost", Path = "/" }.Uri;
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var logAdmission = factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
            await using (var db = await logAdmission.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var device = new Device("replay test", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            await using var scope = factory.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
            var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
            var secret = RandomNumberGenerator.GetBytes(32);
            var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
            var agentId = registration.AgentId!.Value;
            var testRoot = Path.Combine(AppContext.BaseDirectory, "VigiloomAgentReplayTests", Path.GetRandomFileName());
            var stateRoot = Path.Combine(testRoot, "state");
            Exception? primaryFailure = null;
            var preserveDirectory = false;
            try
            {
                Directory.CreateDirectory(testRoot);
                var caPath = Path.Combine(testRoot, "test-root.cer");
                await File.WriteAllBytesAsync(caPath, root.Export(X509ContentType.Cert), token);
                var vault = new IdentityVault(stateRoot);
                await using (var session = vault.Acquire())
                    await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agentId,
                        MachineSecret = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
                var now = DateTimeOffset.UtcNow;
                var messages = Enumerable.Range(0, 3).Select(_ => new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "test", "test", "test")).ToArray();
                var batch = new AgentBatch(1, Guid.CreateVersion7(), now, messages);
                var payload = AgentBatchCodec.Serialize(batch);
                var queue = new DurableBatchQueue(vault, agentId);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, now, messages.Length, payload, token));
                await RunAgentAsync("unavailable", "Agent batch transport: Retry", server, stateRoot, caPath, token, replayOnly: true);
                CollectionAssert.AreEqual(payload, (await queue.PeekAsync(token))!.Payload);
                using (var wrongKey = RSA.Create(2048))
                {
                    var wrongRequest = new CertificateRequest("CN=Wrong Test Root", wrongKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    wrongRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    wrongRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                    using var wrongRoot = wrongRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                    var wrongPath = Path.Combine(testRoot, "wrong-root.cer");
                    await File.WriteAllBytesAsync(wrongPath, wrongRoot.Export(X509ContentType.Cert), token);
                    await RunAgentAsync("normal", "Agent batch transport: Retry", server, stateRoot, wrongPath, token, replayOnly: true);
                }
                CollectionAssert.AreEqual(payload, (await queue.PeekAsync(token))!.Payload);
                await using (var db = await agents.CreateDbContextAsync(token)) Assert.AreEqual(0, await db.ReceivedBatches.CountAsync(token));
                await RunAgentAsync("drop-ack", "TEST_ACK_DROPPED", server, stateRoot, caPath, token, replayOnly: true);
                CollectionAssert.AreEqual(payload, (await queue.PeekAsync(token))!.Payload);
                await using (var db = await agents.CreateDbContextAsync(token))
                {
                    Assert.AreEqual(1, await db.ReceivedBatches.CountAsync(token));
                    Assert.AreEqual(3, await db.RuntimeObservations.CountAsync(token));
                }
                await RunAgentAsync("normal", "Agent batch transport: Delivered", server, stateRoot, caPath, token, replayOnly: true);
                // The replayed batch was at the head, so a different head proves it was removed.
                Assert.AreNotEqual(batch.BatchId, (await queue.PeekAsync(token))?.BatchId);
                Assert.AreEqual(0L, (await queue.GetStatusAsync(token)).QuarantinedBatches);
                await using (var db = await agents.CreateDbContextAsync(token))
                {
                    Assert.AreEqual(1, await db.ReceivedBatches.CountAsync(x => x.BatchId == batch.BatchId, token));
                    var items = await db.RuntimeObservations.AsNoTracking().Where(x => x.BatchId == batch.BatchId).OrderBy(x => x.Ordinal).Select(x => new { x.Ordinal, x.MessageId }).ToArrayAsync(token);
                    CollectionAssert.AreEqual(messages.Select((x, i) => (i, x.MessageId)).ToArray(), items.Select(x => (x.Ordinal, x.MessageId)).ToArray());
                }

                // Run a real metric collector and interrupt its process. The
                // next writer must report uncertain completeness over the
                // authenticated HTTPS heartbeat, without inventing a loss count.
                await RunAgentAsync("unavailable", "Agent metric queue: Queued", server, stateRoot, caPath, token, waitForLedger: true);
                var ledgerPath = Path.Combine(stateRoot, "metric-loss.db");
                Assert.IsTrue(File.Exists(ledgerPath));
                Guid originalLedger;
                string originalWriter;
                using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ledgerPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    db.Open();
                    using var query = db.CreateCommand();
                    query.CommandText = "SELECT ledger_id, writer FROM loss_state WHERE singleton = 1";
                    using var reader = query.ExecuteReader();
                    Assert.IsTrue(reader.Read());
                    originalLedger = Guid.Parse(reader.GetString(0));
                    Assert.IsFalse(reader.IsDBNull(1));
                    originalWriter = reader.GetString(1);
                }
                var owner = new AgentSettingsOwner(agentId, server.AbsoluteUri, vault.StateDirectory);
                await using var ledger = await MetricLossLedger.StartAsync(vault, owner, token);
                var local = await ledger.ReadAsync(token);
                Assert.AreEqual(originalLedger, local.LedgerId);
                Assert.AreNotEqual(originalWriter, ledger.WriterId.ToString("D"));
                Assert.IsTrue(local.CompletenessUnknown);
                await ledger.CompleteAsync(token);

            }
            catch (AgentProcessStillRunningException error) { preserveDirectory = true; primaryFailure = error; throw; }
            catch (Exception error) { primaryFailure = error; throw; }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
                try
                {
                    var prefix = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "VigiloomAgentReplayTests")) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(testRoot).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("Test cleanup path escaped its designated root.");
                    if (!preserveDirectory && Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
                }
                catch (Exception cleanupError) when (primaryFailure is not null) { throw new AggregateException(primaryFailure, cleanupError); }
            }
        }, context.CancellationToken);

    private static async Task RunAgentAsync(string mode, string signal, Uri server, string stateRoot, string caPath, CancellationToken token, bool replayOnly = false, bool waitForLedger = false)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(AgentHarness::AgentHostHarnessMarker).Assembly.Location);
        start.Environment["Vigiloom__Agent__StateDirectory"] = stateRoot;
        start.Environment["Vigiloom__Agent__Server"] = server.AbsoluteUri;
        start.Environment["VIGILOOM_TEST_CA"] = caPath;
        start.Environment["VIGILOOM_TEST_MODE"] = mode;
        start.Environment.Remove("VIGILOOM_TEST_INVENTORY");
        start.Environment.Remove("VIGILOOM_TEST_REPLAY_ONLY");
        if (replayOnly) start.Environment["VIGILOOM_TEST_REPLAY_ONLY"] = "enabled";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test Agent host.");
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Exception? primaryFailure = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            string? line;
            do
            {
                line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null) Assert.Fail($"Test Agent exited before {signal}: {await errors.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token)}");
            } while (!line.Contains(signal, StringComparison.Ordinal));
            if (waitForLedger)
            {
                var ledgerPath = Path.Combine(stateRoot, "metric-loss.db");
                while (true)
                {
                    if (process.HasExited) Assert.Fail("Test Agent exited before the metric loss ledger was ready.");
                    if (File.Exists(ledgerPath))
                    {
                        try
                        {
                            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ledgerPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                            await db.OpenAsync(deadline.Token);
                            using var query = db.CreateCommand();
                            query.CommandText = "SELECT writer FROM loss_state WHERE singleton = 1";
                            if (await query.ExecuteScalarAsync(deadline.Token) is string) break;
                        }
                        catch (SqliteException) { /* The ledger is still being initialized. */ }
                    }
                    await Task.Delay(100, deadline.Token);
                }
            }
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test Agent did not terminate.");
                await errors.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (Exception cleanupError) when (primaryFailure is not null) { throw new AggregateException(primaryFailure, cleanupError); }
        }
    }

    private sealed class AgentProcessStillRunningException(string stateRoot, Exception inner)
        : Exception($"Test Agent may still be running; state retained at {stateRoot}.", inner);
}
