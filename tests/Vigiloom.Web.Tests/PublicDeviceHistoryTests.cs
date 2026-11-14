using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Publishing;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PublicDeviceHistoryTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task History_ReleasesAdmissionOnCancellationOrTimeout(bool serverDeadline) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var template = new PublicationTemplate("History", true, true, now, 1);
        template.ConfirmHistorySince(now.AddHours(-1));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
        { db.Templates.Add(template); await db.SaveChangesAsync(token); }
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            row.SetTemplatePublication("Public device", template.Id, false, now);
            row.ConfirmPublicSince(now.AddHours(-1));
            await db.SaveChangesAsync(token);
        }
        var gate = new PauseBuckets();
        var options = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(gate).Options;
        var clock = new FakeTimeProvider();
        using var tested = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextFactory<NetworkDbContext>>();
            services.AddSingleton<IDbContextFactory<NetworkDbContext>>(new PooledDbContextFactory<NetworkDbContext>(options));
            if (serverDeadline)
            {
                services.RemoveAll<PublicHistoryAdmission>();
                services.AddSingleton(new PublicHistoryAdmission(TimeSpan.FromSeconds(5), clock));
            }
        }));
        using var client = PingTestSite.Client(tested);
        var range = $"?from={Uri.EscapeDataString(now.AddHours(-2).ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}";
        var url = $"/api/v1/public/devices/{device.Id:D}/history{range}";
        var admission = tested.Services.GetRequiredService<PublicHistoryAdmission>();
        Assert.IsTrue(await admission.Gate.WaitAsync(TimeSpan.FromSeconds(5), token));
        try
        {
            Assert.IsTrue(await admission.Gate.WaitAsync(TimeSpan.FromSeconds(5), token));
            try
            {
                using var busy = await client.GetAsync(url, token);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                Assert.AreEqual(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter?.Delta);
            }
            finally { admission.Gate.Release(); }
        }
        finally { admission.Gate.Release(); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var request = client.GetAsync(url, cancellation.Token);
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            if (serverDeadline)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                using var timeout = await request;
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timeout.StatusCode);
                Assert.AreEqual(TimeSpan.FromSeconds(5), timeout.Headers.RetryAfter?.Delta);
            }
            else
            {
                cancellation.Cancel(); gate.Release.TrySetResult();
                await Assert.ThrowsAsync<TaskCanceledException>(async () => await request);
            }
            Assert.IsTrue(await admission.Gate.WaitAsync(TimeSpan.FromSeconds(5), token));
            try { Assert.IsTrue(await admission.Gate.WaitAsync(TimeSpan.FromSeconds(5), token)); admission.Gate.Release(); }
            finally { admission.Gate.Release(); }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            cancellation.Cancel(); gate.Release.TrySetResult();
            try { using var response = await request; } catch when (failure is not null || cancellation.IsCancellationRequested) { }
        }
    }, context.CancellationToken);

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task History_RechecksDeviceAndTemplateAfterConcurrentChange(int action, bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await ReadCommittedSnapshot.ConfigureAsync(database, snapshotReads, token);
            var resourcesOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            var networkOptions = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
            var publishingOptions = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "publishing")).Options;
            await using (var db = new ResourcesDbContext(resourcesOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new NetworkDbContext(networkOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new PublishingDbContext(publishingOptions)) await db.Database.MigrateAsync(token);
            var resources = new PooledDbContextFactory<ResourcesDbContext>(resourcesOptions);
            var now = DateTimeOffset.UtcNow;
            var template = new PublicationTemplate("History", true, true, now, 1);
            template.ConfirmHistorySince(now.AddHours(-1));
            await using (var db = new PublishingDbContext(publishingOptions)) { db.Templates.Add(template); await db.SaveChangesAsync(token); }
            var device = new Device("Device", "example.com", false, now);
            device.SetTemplatePublication("Public device", template.Id, false, now);
            device.ConfirmPublicSince(now.AddHours(-1));
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            var gate = new PauseBuckets();
            var pausedOptions = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(gate).Options;
            var reader = new PublicDeviceHistoryReader(resources, new PooledDbContextFactory<NetworkDbContext>(pausedOptions));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var read = reader.ReadAsync(device.Id, now.AddHours(-2), now, cancellation.Token);
            Exception? failure = null;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                if (action == 0)
                {
                    await using var db = await resources.CreateDbContextAsync(token);
                    (await db.Devices.SingleAsync(x => x.Id == device.Id, token)).SetPublication(false, null, null, now);
                    await db.SaveChangesAsync(token);
                }
                else
                {
                    await using var db = new PublishingDbContext(publishingOptions);
                    var row = await db.Templates.SingleAsync(x => x.Id == template.Id, token);
                    if (action == 1) row.Update(row.Name, true, true, now, 0); else row.Retire(now);
                    await db.SaveChangesAsync(token);
                }
                gate.Release.TrySetResult();
                Assert.IsNull(await read);
            }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                gate.Release.TrySetResult();
                if (!read.IsCompleted) await cancellation.CancelAsync();
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
    public Task History_RequiresExplicitAuthorizationAndClipsPeriods() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var original = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var aligned = now.AddTicks(-(now.UtcTicks % TimeSpan.FromMinutes(5).Ticks));
        var template = new PublicationTemplate("History", true, true, now, 1);
        template.ConfirmHistorySince(aligned.AddMinutes(-16));
        var pending = new PublicationTemplate("Pending", true, true, now, 1);
        var disabled = new PublicationTemplate("Disabled", true, true, now);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
        { db.Templates.AddRange(template, pending, disabled); await db.SaveChangesAsync(token); }
        var hidden = new Device("Private", "10.0.0.7", true, now);
        var waiting = new Device("Pending", "10.0.0.8", true, now);
        waiting.SetTemplatePublication("Pending", pending.Id, false, now);
        waiting.ConfirmPublicSince(aligned.AddHours(-1));
        var withoutHistory = new Device("No history", "10.0.0.9", false, now);
        withoutHistory.SetTemplatePublication("No history", disabled.Id, false, now);
        withoutHistory.ConfirmPublicSince(aligned.AddHours(-1));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            var row = await db.Devices.SingleAsync(x => x.Id == original.Id, token);
            row.SetTemplatePublication("Public network", template.Id, false, now);
            row.ConfirmPublicSince(aligned.AddMinutes(-31));
            db.Devices.AddRange(hidden, waiting, withoutHistory);
            await db.SaveChangesAsync(token);
        }
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            foreach (var (when, target, location, value) in new[]
            {
                (aligned.AddDays(-2), original.Address, "central", 90d),
                (aligned.AddMinutes(-20), original.Address, "central", 80d),
                (aligned.AddMinutes(-15), original.Address, "central", 15d),
                (aligned.AddMinutes(-10), original.Address, "central", 10d),
                (aligned.AddMinutes(-5), "other.example", "central", 70d),
                (aligned.AddMinutes(-5), original.Address, "other", 60d)
            })
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [network].[PingRollups] ([DeviceId], [Target], [Location], [WindowStart], [Attempts], [Successes], [Timeouts], [Unreachable], [DnsErrors], [PermissionDenied], [MonitorErrors], [UnknownFailures], [LatencySum], [LatencyOverflow], [FirstOccurredAt], [LastOccurredAt])
                    VALUES ({original.Id}, {target}, {location}, {when}, 1, 1, 0, 0, 0, 0, 0, 0, {value}, 0, {when}, {when})
                    """, token);
        }
        using var client = PingTestSite.Client(host);
        var range = $"?from={Uri.EscapeDataString(aligned.AddDays(-3).ToString("O"))}&to={Uri.EscapeDataString(aligned.ToString("O"))}";
        string Url(Guid id) => $"/api/v1/public/devices/{id:D}/history{range}";
        foreach (var id in new[] { hidden.Id, waiting.Id, withoutHistory.Id, Guid.CreateVersion7() })
        { using var response = await client.GetAsync(Url(id), token); Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode); }
        using (var response = await client.GetAsync(Url(original.Id), token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain(original.Address, json);
            Assert.DoesNotContain("other.example", json);
            var history = System.Text.Json.JsonSerializer.Deserialize<PublicDeviceHistory>(json, System.Text.Json.JsonSerializerOptions.Web);
            Assert.IsNotNull(history);
            CollectionAssert.AreEqual(new double?[] { 15d, 10d }, history.Points.Select(x => x.AverageLatencyMilliseconds).ToArray());
        }
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [resources].[Devices] SET [PublicSince] = {aligned.AddMinutes(-12)} WHERE [Id] = {original.Id}", token);
        var deviceBounded = await client.GetFromJsonAsync<PublicDeviceHistory>(Url(original.Id), token);
        Assert.IsNotNull(deviceBounded);
        CollectionAssert.AreEqual(new double?[] { 10d }, deviceBounded.Points.Select(x => x.AverageLatencyMilliseconds).ToArray());
        // When publication is older than the template allowance, the configured day limit still applies.
        var oldStart = aligned.AddDays(-3);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [resources].[Devices] SET [PublicSince] = {oldStart} WHERE [Id] = {original.Id}", token);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [publishing].[Templates] SET [HistorySince] = {oldStart} WHERE [Id] = {template.Id}", token);
        var bounded = await client.GetFromJsonAsync<PublicDeviceHistory>(Url(original.Id), token);
        Assert.IsNotNull(bounded);
        CollectionAssert.AreEqual(new double?[] { 80d, 15d, 10d }, bounded.Points.Select(x => x.AverageLatencyMilliseconds).ToArray());
        var shortRange = $"?from={Uri.EscapeDataString(aligned.AddMinutes(-12).ToString("O"))}&to={Uri.EscapeDataString(aligned.ToString("O"))}";
        var shortened = await client.GetFromJsonAsync<PublicDeviceHistory>($"/api/v1/public/devices/{original.Id:D}/history{shortRange}", token);
        Assert.IsNotNull(shortened);
        CollectionAssert.AreEqual(new double?[] { 10d }, shortened.Points.Select(x => x.AverageLatencyMilliseconds).ToArray());
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
        {
            var row = await db.Templates.SingleAsync(x => x.Id == template.Id, token);
            row.Update(row.Name, true, true, now, 0);
            await db.SaveChangesAsync(token);
        }
        using (var response = await client.GetAsync(Url(original.Id), token)) Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }, context.CancellationToken);
}
