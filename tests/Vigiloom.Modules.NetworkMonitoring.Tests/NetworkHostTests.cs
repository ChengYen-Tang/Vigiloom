extern alias NetworkHost;
extern alias GatewayHost;
extern alias OperationsHost;

using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Hangfire;
using Hangfire.SqlServer;
using Hangfire.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NetworkHostTests(TestContext context)
{
    [TestMethod]
    public void SpeedtestHttpHandlersArePooledAcrossMeasurements()
    {
        using var network = new WebApplicationFactory<NetworkHost::Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:Network", "Server=localhost;Database=unused;Integrated Security=true;Encrypt=false");
            web.UseSetting("Hangfire:Enabled", "false");
            web.UseSetting("Temporal:EnableNetworkWorker", "false");
            web.ConfigureTestServices(services =>
            {
                foreach (var item in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                    x.ImplementationType?.Name is "WorkDispatcher" or "PingWorker" or "SpeedWorker" or "NetworkDiagnosticsPublisher").ToArray()) services.Remove(item);
            });
        });
        using var startup = network.CreateClient();
        var factory = network.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        using var first = factory.CreateHandler(SpeedtestNetProbe.HttpClientName);
        using var second = factory.CreateHandler(SpeedtestNetProbe.HttpClientName);
        Assert.AreSame(first, second);
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public Task ReportsScannerStateToIndependentOperationsService() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (networkDb, outer) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, token) =>
        {
            var networkOptions = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(networkDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
            await using (var db = new NetworkDbContext(networkOptions)) await db.Database.MigrateAsync(token);
            var scheduleOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(networkDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(scheduleOptions)) await db.Database.MigrateAsync(token);
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            using var operations = new WebApplicationFactory<OperationsHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Production");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:EnableOperationsWorker", "false");
                web.UseSetting("ConnectionStrings:Operations", operationsDb.ConnectionString);
                web.UseSetting("ConnectionStrings:Scheduling", operationsDb.ConnectionString);
            });
            operations.UseKestrel(0);
            using var operationsClient = operations.CreateClient();
            var address = operations.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var network = new WebApplicationFactory<NetworkHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Production");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:EnableNetworkWorker", "false");
                web.UseSetting("ConnectionStrings:Network", networkDb.ConnectionString);
                web.UseSetting("Services:Resources:Url", "http://127.0.0.1:1");
                web.UseSetting("Services:Operations:Url", address);
            });
            using var networkClient = network.CreateClient();
            WorkerDiagnosticsInfo? received = null;
            for (var i = 0; i < 80; i++)
            {
                received = await operationsClient.GetFromJsonAsync<WorkerDiagnosticsInfo>("/internal/v1/diagnostics/workers", token);
                if (received!.Workers.Any(x => x.Ping.LastSuccess != null || x.Ping.LastFailure != null)) break;
                await Task.Delay(250, token);
            }
            Assert.IsTrue(received!.Workers.Any(x => x.Ping.LastSuccess != null || x.Ping.LastFailure != null));
        }, outer), context.CancellationToken);

    [TestMethod]
    public Task IndependentHostRunsPingAndQueuesTransition() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var networkOptions = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new NetworkDbContext(networkOptions)) await db.Database.MigrateAsync(token);
        await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
        var setup = new SqlServerStorage(database.ConnectionString, SchedulingRegistration.StorageOptions(prepareSchema: true));
        using (setup.GetConnection()) { }
        var id = Guid.CreateVersion7();
        var version = new byte[8];
        var published = true;
        var withdrawDuringRead = false;
        var policyReads = 0;
        var now = DateTimeOffset.UtcNow;
        var bucket = TimeSpan.FromMinutes(5);
        var allowedBucket = now.AddTicks(-(now.UtcTicks % bucket.Ticks)).AddMinutes(-10);
        var publishedSince = allowedBucket.AddMinutes(-2);
        var resourceBuilder = WebApplication.CreateSlimBuilder();
        resourceBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var resources = resourceBuilder.Build();
        resources.MapGet("/internal/v1/device-management/{deviceId:guid}", (Guid deviceId) =>
            deviceId == id ? Results.Ok(new DeviceDetails(id, "Example", "example.org", [], false, true, false,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Convert.ToBase64String(version))) : Results.NotFound());
        resources.MapGet("/internal/v1/publications/devices/{deviceId:guid}", (Guid deviceId) =>
        {
            if (withdrawDuringRead && Interlocked.Increment(ref policyReads) == 2) published = false;
            return deviceId == id && published ? Results.Ok(new PublishedDevicePolicy(id, "Public Example", "example.org", true, false, false,
                publishedSince, 1, publishedSince, Convert.ToBase64String(version), "template")) : Results.NotFound();
        });
        await resources.StartAsync(token);
        var resourceAddress = resources.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using (var db = new NetworkDbContext(networkOptions))
        {
            db.PingTargets.Add(new PingTargetState { DeviceId = id, Target = "example.org", DeviceVersion = version,
                Enabled = true, Successes = 1, DueAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            foreach (var start in new[] { allowedBucket.AddMinutes(-5), allowedBucket })
            {
                var rollup = new PingRollup(id, "example.org", "central", start);
                rollup.Add(new PingSample(id, "example.org", null, "central", 1, start, start, PingOutcome.Success, 3));
                db.PingRollups.Add(rollup);
            }
            await db.SaveChangesAsync(token);
        }
        using var host = new WebApplicationFactory<NetworkHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseUrls("http://127.0.0.1:0");
            builder.UseSetting("Hangfire:Enabled", "true");
            builder.UseSetting("Temporal:EnableNetworkWorker", "false");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Network"] = database.ConnectionString, ["ConnectionStrings:Scheduling"] = database.ConnectionString, ["Hangfire:Enabled"] = "true", ["Services:Identity:Url"] = "http://identity",
                ["Vigiloom:Gateway:TrustedProxies:0"] = "127.0.0.1", ["Services:Resources:Url"] = resourceAddress }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataProtectionProvider>();
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.RemoveAll<IApiCredentialValidation>();
                services.AddSingleton<IApiCredentialValidation, CredentialValidation>();
                services.RemoveAll<IDevicePingSource>();
                services.AddSingleton<IDevicePingSource>(new Source(id, version));
                services.RemoveAll<IPingProbe>();
                services.AddSingleton<IPingProbe, Probe>();
                services.RemoveAll<IOutboxHandler>();
                services.AddSingleton<IOutboxHandler, Handler>();
                foreach (var item in services.Where(item => item.ImplementationType?.Name is "WorkDispatcher" or "SpeedWorker").ToArray()) services.Remove(item);
            });
        });
        host.UseKestrel(0);
        host.StartServer();
        using (var jobs = host.Services.GetRequiredService<JobStorage>().GetConnection())
        {
            var recurring = jobs.GetRecurringJobs();
            foreach (var jobId in new[] { "vigiloom-speed-test", "vigiloom-speed-retention", "vigiloom-ping-retention", "vigiloom-ping-rollup-backfill" })
            {
                var job = recurring.Single(item => item.Id == jobId);
                Assert.AreEqual("network", job.Job.Queue);
            }
        }
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.23");
        using (var response = await client.GetAsync("/api/v1/speed-tests?from=2026-01-01T00%3A00%3A00Z&to=2026-01-02T00%3A00%3A00Z", token))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync("/api/v1/settings/speed-tests", token))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync($"/api/v1/devices/{id:D}/ping", token))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
        using (var response = await client.GetAsync("/api/v1/speed-tests?from=2026-01-01T00%3A00%3A00Z&to=2026-01-02T00%3A00%3A00Z", token))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await client.GetAsync("/api/v1/settings/speed-tests", token))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await client.GetAsync($"/api/v1/devices/{id:D}/ping", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var status = await response.Content.ReadFromJsonAsync<DevicePingStatus>(cancellationToken: token);
            Assert.IsNotNull(status);
            Assert.AreEqual("example.org", Assert.ContainsSingle(status.Targets).Target);
        }
        using (var response = await client.PostAsJsonAsync("/internal/v1/ping/statuses", new[] { new PingDeviceSnapshot(id, version, ["example.org"]) }, token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statuses = await response.Content.ReadFromJsonAsync<Dictionary<Guid, DevicePingStatus>>(cancellationToken: token);
            Assert.IsNotNull(statuses);
            Assert.AreEqual("example.org", Assert.ContainsSingle(statuses[id].Targets).Target);
        }
        using (var response = await client.PostAsJsonAsync("/internal/v1/speed/history", new SpeedHistoryQuery(now.AddDays(-1), now), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsNotNull(await response.Content.ReadFromJsonAsync<SpeedHistoryPage>(cancellationToken: token));
        }
        using (var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:network:Destinations:network:Address"] = address,
                ["Services:Identity:Url"] = "http://identity"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IApiCredentialValidation>();
                services.AddSingleton<IApiCredentialValidation, CredentialValidation>();
            });
        }))
        using (var throughGateway = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }))
        {
            using (var denied = await throughGateway.GetAsync("/api/v1/speed-tests?from=2026-01-01T00%3A00%3A00Z&to=2026-01-02T00%3A00%3A00Z", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            throughGateway.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
            using (var allowed = await throughGateway.GetAsync("/api/v1/speed-tests?from=2026-01-01T00%3A00%3A00Z&to=2026-01-02T00%3A00%3A00Z", token))
                Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
            using (var internalApi = await throughGateway.GetAsync("/internal/v1/ping/settings", token))
                Assert.AreEqual(HttpStatusCode.NotFound, internalApi.StatusCode);
        }
        using (var response = await client.PostAsJsonAsync("/internal/v1/ping/history", new PingHistoryQuery(id, allowedBucket, allowedBucket.AddMinutes(5), "example.org", "central"), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsNotNull(await response.Content.ReadFromJsonAsync<PingHistoryPage>(cancellationToken: token));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var db = new NetworkDbContext(networkOptions);
            if (await db.PingSamples.AnyAsync(deadline.Token)) break;
            await Task.Delay(100, deadline.Token);
        }
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox";
        Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
        var actor = Guid.CreateVersion7();
        var pingSettings = await client.GetFromJsonAsync<PingSettingsSnapshot>("/internal/v1/ping/settings", token);
        Assert.IsNotNull(pingSettings);
        var pingInput = new PingSettingsInput(pingSettings.IntervalSeconds, pingSettings.TimeoutSeconds, pingSettings.FailureThreshold,
            pingSettings.SuccessThreshold, pingSettings.MaxConcurrency, pingSettings.Version);
        using (var response = await client.PostAsJsonAsync("/internal/v1/ping/settings", new PingSettingsCommand(pingInput, actor), token))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await client.PostAsJsonAsync("/internal/v1/ping/settings", new PingSettingsCommand(pingInput, actor), token))
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var pingRetention = await client.GetFromJsonAsync<PingRetentionSnapshot>("/internal/v1/ping/retention", token);
        Assert.IsNotNull(pingRetention);
        var pingPreviewRequest = new PingRetentionPreviewRequest(1, 1, pingRetention.Version);
        using (var response = await client.PostAsJsonAsync("/internal/v1/ping/retention/preview", new PingRetentionPreviewCommand(pingPreviewRequest, actor), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var preview = await response.Content.ReadFromJsonAsync<PingRetentionPreview>(cancellationToken: token);
            Assert.IsNotNull(preview);
            using var update = await client.PostAsJsonAsync("/internal/v1/ping/retention", new PingRetentionCommand(new PingRetentionInput(1, 1, pingRetention.Version, preview.ConfirmationToken), actor), token);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
        }
        var speedSettings = await client.GetFromJsonAsync<SpeedSettingsSnapshot>("/internal/v1/speed/settings", token);
        Assert.IsNotNull(speedSettings);
        var input = new SpeedSettingsInput(false, 6, 2, 40, speedSettings.Version);
        using (var response = await client.PostAsJsonAsync("/internal/v1/speed/settings", new SpeedSettingsCommand(input, actor), token))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using (var response = await client.PostAsJsonAsync("/internal/v1/speed/settings", new SpeedSettingsCommand(input, actor), token))
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var retention = await client.GetFromJsonAsync<SpeedRetentionSnapshot>("/internal/v1/speed/retention", token);
        Assert.IsNotNull(retention);
        using (var response = await client.PostAsJsonAsync("/internal/v1/speed/retention/preview", new SpeedRetentionPreviewCommand(30, retention.Version, actor), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var preview = await response.Content.ReadFromJsonAsync<SpeedRetentionPreview>(cancellationToken: token);
            Assert.IsNotNull(preview);
            using var update = await client.PostAsJsonAsync("/internal/v1/speed/retention", new SpeedRetentionCommand(new SpeedRetentionInput(30, retention.Version, preview.ConfirmationToken), actor), token);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
        }
        using (var response = await client.PostAsync($"/internal/v1/speed/{Guid.CreateVersion7():D}/cancel", null, token))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        using (var response = await client.GetAsync($"/api/v1/public/devices/{id:D}/ping", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse((await response.Content.ReadAsStringAsync(token)).Contains("example.org", StringComparison.Ordinal));
            var result = await response.Content.ReadFromJsonAsync<PublicDevice>(cancellationToken: token);
            Assert.IsNotNull(result);
            Assert.AreEqual("Public Example", result.Name);
            Assert.IsNull(result.LatencyMilliseconds);
            Assert.IsNull(result.ObservedAt);
        }
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"));
        using (var response = await client.GetAsync($"/api/v1/public/devices/{id:D}/ping/history?from={from}&to={to}", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var history = await response.Content.ReadFromJsonAsync<PublicDeviceHistory>(cancellationToken: token);
            Assert.IsNotNull(history);
            Assert.AreEqual(allowedBucket, Assert.ContainsSingle(history.Points).From);
        }
        using (var response = await client.GetAsync($"/internal/v1/ping/{id:D}/publication-preview", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var history = await response.Content.ReadFromJsonAsync<PublicDeviceHistory>(cancellationToken: token);
            Assert.IsNotNull(history);
            Assert.AreEqual(allowedBucket, Assert.ContainsSingle(history.Points).From);
        }
        using (var response = await client.PostAsJsonAsync("/internal/v1/public-ping", new[] { id }, token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var batch = await response.Content.ReadFromJsonAsync<PublicDevice[]>(cancellationToken: token);
            Assert.IsNotNull(batch);
            Assert.AreEqual(id, Assert.ContainsSingle(batch).Id);
        }
        withdrawDuringRead = true;
        policyReads = 0;
        using (var response = await client.GetAsync($"/api/v1/public/devices/{id:D}/ping", token))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await client.GetAsync($"/api/v1/public/devices/{id:D}/ping/history?from={from}&to={to}", token))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }, context.CancellationToken);

    private sealed class Source(Guid id, byte[] version) : IDevicePingSource
    {
        public async IAsyncEnumerable<PingDeviceSnapshot> ReadEnabledAsync([EnumeratorCancellation] CancellationToken token)
        { yield return new(id, version, ["example.org"]); await Task.CompletedTask; }
        public Task<bool> IsCurrentAsync(Guid id, byte[] version, string target, DbConnection connection, DbTransaction transaction, CancellationToken token) => Task.FromResult(true);
        public Task<PingDeviceContext> GetContextAsync(Guid id, DbConnection connection, DbTransaction transaction, CancellationToken token) => Task.FromResult(new PingDeviceContext("Example", []));
    }
    private sealed class Probe : IPingProbe
    {
        public Task<PingMeasurement> MeasureAsync(string target, int timeoutSeconds, CancellationToken token) => Task.FromResult(new PingMeasurement(PingOutcome.Success, target, 1));
    }
    private sealed class Handler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class CredentialValidation : IApiCredentialValidation
    {
        public Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token) =>
            Task.FromResult<VerifiedApiCredentialResponse?>(value == "valid" ? new(Guid.CreateVersion7(), "test", false) : null);
    }
}
