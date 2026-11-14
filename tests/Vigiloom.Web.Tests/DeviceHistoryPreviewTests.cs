using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Web.Publishing;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DeviceHistoryPreviewTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Preview_RechecksDeviceAfterConcurrentRevocation(bool archive) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await resources.CreateDbContextAsync(token))
        {
            var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            row.SetPublication(true, "Network", PublicationPolicy.BaselineVersion, now);
            await db.SaveChangesAsync(token);
        }
        var gate = new PauseBuckets();
        var options = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(gate).Options;
        var network = new PooledDbContextFactory<NetworkDbContext>(options);
        var preview = new DeviceHistoryPreviewReader(resources, network);
        Assert.IsNull(await preview.ReadAsync(device.Id, token)); // Pending publication never exposes historical data.
        await using (var db = await resources.CreateDbContextAsync(token))
        {
            var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            row.ConfirmPublicSince(now.AddHours(-1));
            await db.SaveChangesAsync(token);
        }
        var read = preview.ReadAsync(device.Id, token);
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            await using (var db = await resources.CreateDbContextAsync(token))
            {
                var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                if (archive) row.Archive(now); else row.SetPublication(false, null, null, now);
                await db.SaveChangesAsync(token);
            }
            gate.Release.TrySetResult();
            Assert.IsNull(await read);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            gate.Release.TrySetResult();
            try { await read; } catch when (failure is not null) { }
        }
    }, context.CancellationToken);

    private sealed class PauseBuckets : DbCommandInterceptor
    {
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[network].[PingRollups]", StringComparison.Ordinal) && Interlocked.Exchange(ref entered, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Preview_UsesOnlyCompleteCentralBucketsAfterPublication() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var aligned = now.AddTicks(-(now.UtcTicks % TimeSpan.FromMinutes(5).Ticks));
        var since = aligned.AddMinutes(-16);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            row.SetPublication(true, "Public network", PublicationPolicy.BaselineVersion, now);
            row.ConfirmPublicSince(since);
            await db.SaveChangesAsync(token);
        }
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            foreach (var (offset, target, location, latency) in new[]
            {
                (-20, device.Address, "central", 10d), (-15, device.Address, "central", 20d),
                (-10, device.Address, "central", 30d), (-5, "other.example", "central", 40d),
                (-5, device.Address, "other", 50d), (5, device.Address, "central", 60d)
            })
            {
                var when = aligned.AddMinutes(offset);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [network].[PingRollups] ([DeviceId], [Target], [Location], [WindowStart], [Attempts], [Successes], [Timeouts], [Unreachable], [DnsErrors], [PermissionDenied], [MonitorErrors], [UnknownFailures], [LatencySum], [LatencyOverflow], [FirstOccurredAt], [LastOccurredAt])
                    VALUES ({device.Id}, {target}, {location}, {when}, 1, 1, 0, 0, 0, 0, 0, 0, {latency}, 0, {when}, {when})
                    """, token);
            }
            var failed = aligned.AddMinutes(-5);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [network].[PingRollups] ([DeviceId], [Target], [Location], [WindowStart], [Attempts], [Successes], [Timeouts], [Unreachable], [DnsErrors], [PermissionDenied], [MonitorErrors], [UnknownFailures], [LatencySum], [LatencyOverflow], [FirstOccurredAt], [LastOccurredAt])
                VALUES ({device.Id}, {device.Address}, {"central"}, {failed}, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, {failed}, {failed})
                """, token);
        }
        using var anonymous = PingTestSite.Client(host);
        using var reader = PingTestSite.Client(host);
        using var admin = PingTestSite.Client(host);
        await PingTestSite.LoginAsync(reader, "reader", token);
        await PingTestSite.LoginAsync(admin, "admin", token);
        var url = $"/api/v1/admin/publications/devices/{device.Id:D}/history-preview";
        using (var response = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await admin.GetAsync(url, token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain(device.Address, body);
            var result = System.Text.Json.JsonSerializer.Deserialize<PublicDeviceHistory>(body, System.Text.Json.JsonSerializerOptions.Web);
            Assert.IsNotNull(result);
            CollectionAssert.AreEqual(new double?[] { 20d, 30d, null }, result.Points.Select(x => x.AverageLatencyMilliseconds).ToArray());
        }
        var range = $"?from={Uri.EscapeDataString(aligned.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(aligned.ToString("O"))}";
        using (var response = await anonymous.GetAsync($"/api/v1/public/devices/{device.Id:D}/history{range}", token)) Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        // An older publication must still be bounded to seven days.
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [resources].[Devices] SET [PublicSince] = {aligned.AddDays(-9)} WHERE [Id] = {device.Id}", token);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            foreach (var (days, latency) in new[] { (-8, 80d), (-6, 60d) })
            {
                var when = aligned.AddDays(days);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [network].[PingRollups] ([DeviceId], [Target], [Location], [WindowStart], [Attempts], [Successes], [Timeouts], [Unreachable], [DnsErrors], [PermissionDenied], [MonitorErrors], [UnknownFailures], [LatencySum], [LatencyOverflow], [FirstOccurredAt], [LastOccurredAt])
                    VALUES ({device.Id}, {device.Address}, {"central"}, {when}, 1, 1, 0, 0, 0, 0, 0, 0, {latency}, 0, {when}, {when})
                    """, token);
            }
        }
        var bounded = await admin.GetFromJsonAsync<PublicDeviceHistory>(url, token);
        Assert.IsNotNull(bounded);
        CollectionAssert.AreEqual(new double?[] { 60d, 10d, 20d, 30d, null }, bounded.Points.Select(x => x.AverageLatencyMilliseconds).ToArray());
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            row.SetPublication(false, null, null, now);
            await db.SaveChangesAsync(token);
        }
        using (var response = await admin.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }, context.CancellationToken);
}
