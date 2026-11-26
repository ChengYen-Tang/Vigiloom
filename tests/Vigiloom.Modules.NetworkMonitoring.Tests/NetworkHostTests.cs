extern alias NetworkHost;

using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.NetworkMonitoring.Application;
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
    public Task IndependentHostRunsPingAndQueuesTransition() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var networkOptions = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new NetworkDbContext(networkOptions)) await db.Database.MigrateAsync(token);
        await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
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
            builder.UseSetting("Hangfire:Enabled", "false");
            builder.UseSetting("Temporal:EnableNetworkWorker", "false");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Network"] = database.ConnectionString, ["Hangfire:Enabled"] = "false", ["Services:Identity:Url"] = "http://identity",
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
