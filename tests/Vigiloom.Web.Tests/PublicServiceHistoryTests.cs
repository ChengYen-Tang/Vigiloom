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
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Api;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PublicServiceHistoryTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task CancelledHistory_ReleasesAdmissionSlot(bool serverDeadline) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var normal = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-5);
        var source = new ServiceSource("source", new byte[32], start);
        var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", start);
        var service = new MonitoredService(candidate, start);
        service.SetPublication(true, "Orders", PublicationPolicy.ServiceHistoryVersion, start);
        service.ConfirmPublicSince(start);
        var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 3, 2, start);
        var interval = new HealthStateInterval(target.Id, 1, 1, start);
        interval.Close(now.AddMinutes(-1));
        await using (var db = await normal.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            db.HealthTargets.Add(target); db.HealthStateIntervals.Add(interval);
            await db.SaveChangesAsync(token);
        }
        var gate = new PauseIntervals();
        var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(gate).Options;
        var contexts = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
        var timeoutClock = new FakeTimeProvider();
        using var tested = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextFactory<ServiceObservabilityDbContext>>();
            services.AddSingleton<IDbContextFactory<ServiceObservabilityDbContext>>(contexts);
            if (serverDeadline)
            {
                services.RemoveAll<PublicHistoryAdmission>();
                services.AddSingleton(new PublicHistoryAdmission(TimeSpan.FromSeconds(5), timeoutClock));
            }
        }));
        using var client = PingTestSite.Client(tested);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var range = $"?from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(now.AddMinutes(-1).ToString("O"))}";
        var request = client.GetAsync($"/api/v1/public/services/{service.Id:D}/history{range}", cancellation.Token);
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            if (serverDeadline)
            {
                timeoutClock.Advance(TimeSpan.FromSeconds(5));
                using var timedOut = await request;
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
                Assert.AreEqual(TimeSpan.FromSeconds(5), timedOut.Headers.RetryAfter?.Delta);
            }
            else
            {
                cancellation.Cancel();
                gate.Release.TrySetResult();
                await Assert.ThrowsAsync<TaskCanceledException>(async () => await request);
            }
            var admission = tested.Services.GetRequiredService<PublicHistoryAdmission>();
            Assert.IsTrue(await admission.Gate.WaitAsync(TimeSpan.FromSeconds(5), token));
            try { Assert.IsTrue(await admission.Gate.WaitAsync(TimeSpan.FromSeconds(5), token)); admission.Gate.Release(); }
            finally { admission.Gate.Release(); }
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            cancellation.Cancel(); gate.Release.TrySetResult();
            try { using var response = await request; } catch when (failure is not null || cancellation.IsCancellationRequested) { }
        }
    }, context.CancellationToken);

    [TestMethod]
    [DataRow(PublicationPolicy.ServiceHistoryVersion)]
    [DataRow(PublicationPolicy.ServiceHistoryDetailsVersion)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task HistoryPolicy_RequiresAdministratorAndStartsNewPeriod(string requestedPolicy) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var old = DateTimeOffset.UtcNow.AddDays(-1);
        var source = new ServiceSource("source", new byte[32], old);
        var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", old);
        var service = new MonitoredService(candidate, old);
        service.SetPublication(true, "Orders", requestedPolicy == PublicationPolicy.ServiceHistoryVersion ? PublicationPolicy.BaselineVersion : PublicationPolicy.ServiceHistoryVersion, old);
        service.ConfirmPublicSince(old);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            await db.SaveChangesAsync(token);
        }
        using var reader = PingTestSite.Client(host);
        using var admin = PingTestSite.Client(host);
        await PingTestSite.LoginAsync(reader, "reader", token);
        await PingTestSite.LoginAsync(admin, "admin", token);
        var csrf = (await admin.GetFromJsonAsync<AntiforgeryResponse>("/api/v1/session/antiforgery", token))!.RequestToken;
        admin.DefaultRequestHeaders.Add("RequestVerificationToken", csrf);
        var url = $"/api/v1/admin/publications/services/{service.Id:D}";
        var settings = await admin.GetFromJsonAsync<PublicationSettings>(url, token);
        Assert.IsNotNull(settings);
        var requested = new PublicationChange(true, "Orders", requestedPolicy, settings.Version);
        using (var denied = await reader.PutAsJsonAsync(url, requested, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var updated = await admin.PutAsJsonAsync(url, requested, token);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync(token));
        var result = await updated.Content.ReadFromJsonAsync<PublicationSettings>(cancellationToken: token);
        Assert.IsNotNull(result);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            var persisted = await db.Services.AsNoTracking().SingleAsync(x => x.Id == service.Id, token);
            Assert.AreEqual(requestedPolicy, persisted.PublicPolicyVersion);
            Assert.IsNotNull(persisted.PublicSince);
            Assert.IsGreaterThan(before, persisted.PublicSince.Value);
            Assert.AreEqual(Convert.ToBase64String(persisted.Version), result.Version);
        }
        using (var scope = host.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<OperationAuditQueryService>()
                .QueryPublicationsAsync(new OperationAuditQuery(before, DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.IsTrue(audit.Items.Any(x => x.ResourceId == service.Id && x.Publication is { PolicyChanged: true }));
        }
        using (var auditPage = await admin.GetAsync("/settings/audit?source=publications", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, auditPage.StatusCode);
            Assert.Contains("公開歷史設定已變更", await auditPage.Content.ReadAsStringAsync(token));
        }
        using var revoked = await admin.PutAsJsonAsync(url, new PublicationChange(false, null, null, result.Version), token);
        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode);
        using var anonymous = PingTestSite.Client(host);
        var range = $"?from={Uri.EscapeDataString(old.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}";
        using var hidden = await anonymous.GetAsync($"/api/v1/public/services/{service.Id:D}/history{range}", token);
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
    }, context.CancellationToken);

    [TestMethod]
    public void DailySummary_ClipsMidnightAndExcludesUnknownFromAvailability()
    {
        var start = new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero);
        var midnight = start.AddMinutes(30);
        var end = midnight.AddMinutes(30);
        var intervals = new[]
        {
            new ServiceHealthInterval(start, midnight, ServiceHealthState.Healthy, 0),
            new ServiceHealthInterval(midnight, midnight.AddMinutes(10), ServiceHealthState.Degraded, 0),
            new ServiceHealthInterval(midnight.AddMinutes(10), midnight.AddMinutes(20), ServiceHealthState.Healthy, 0),
            new ServiceHealthInterval(midnight.AddMinutes(20), end, ServiceHealthState.Unknown, 1)
        };
        var days = PublicServiceHistoryReader.ToDays(start, end, intervals);
        Assert.HasCount(2, days);
        Assert.AreEqual(midnight, days[0].To);
        Assert.AreEqual("Healthy", days[0].State);
        Assert.AreEqual("Degraded", days[1].State);
        Assert.AreEqual(0.5, days[1].Availability!.Value, 0.000001);
        Assert.AreEqual(2.0 / 3.0, days[1].Coverage, 0.000001);
        Assert.IsTrue(PublicServiceHistoryReader.Valid(start, start.AddDays(90)));
        Assert.IsFalse(PublicServiceHistoryReader.Valid(start, start.AddDays(90).AddTicks(1)));
    }

    [TestMethod]
    public void Periods_MergeEquivalentPublicStatesAndDoNotInventBoundaryChanges()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ServiceHealthInterval[] input =
        [
            new(start, start.AddMinutes(1), ServiceHealthState.Healthy, 0),
            new(start.AddMinutes(1), start.AddMinutes(2), ServiceHealthState.Healthy, 1),
            new(start.AddMinutes(2), start.AddMinutes(3), ServiceHealthState.Degraded, 0),
            new(start.AddMinutes(3), start.AddMinutes(4), ServiceHealthState.Unknown, 2)
        ];
        var result = PublicServiceHistoryReader.ToPeriods(input);
        Assert.HasCount(3, result);
        Assert.AreEqual(new PublicHealthPeriod(start, start.AddMinutes(2), "Healthy", false), result[0]);
        Assert.AreEqual(new PublicHealthPeriod(start.AddMinutes(2), start.AddMinutes(3), "Degraded", true), result[1]);
        Assert.AreEqual(new PublicHealthPeriod(start.AddMinutes(3), start.AddMinutes(4), "Unknown", true), result[2]);
        var tooMany = Enumerable.Range(0, 513).Select(i => new ServiceHealthInterval(start.AddSeconds(i), start.AddSeconds(i + 1),
            i % 2 == 0 ? ServiceHealthState.Healthy : ServiceHealthState.Unhealthy, 0)).ToArray();
        Assert.HasCount(512, PublicServiceHistoryReader.ToPeriods(tooMany[..512]));
        Assert.ThrowsExactly<HealthHistoryTooLargeException>(() => PublicServiceHistoryReader.ToPeriods(tooMany));
        tooMany[^1] = tooMany[^1] with { State = tooMany[^2].State };
        Assert.HasCount(512, PublicServiceHistoryReader.ToPeriods(tooMany));
        Assert.IsTrue(PublicServiceHistoryReader.ValidPeriods(start, start.AddDays(1)));
        Assert.IsFalse(PublicServiceHistoryReader.ValidPeriods(start, start.AddDays(1).AddTicks(1)));
    }

    [TestMethod]
    [DataRow(0, false, false)]
    [DataRow(1, false, false)]
    [DataRow(0, true, false)]
    [DataRow(1, true, false)]
    [DataRow(0, false, true)]
    [DataRow(1, false, true)]
    [DataRow(2, false, true)]
    [DataRow(0, true, true)]
    [DataRow(1, true, true)]
    [DataRow(2, true, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task History_RechecksPublicationAndSourceAfterReading(int mutation, bool snapshotReads, bool periods) => WithHistoryReadMode(snapshotReads, async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var normal = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-5);
        var source = new ServiceSource("source", new byte[32], start);
        var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", start);
        var service = new MonitoredService(candidate, start);
        service.SetPublication(true, "Orders", periods ? PublicationPolicy.ServiceHistoryDetailsVersion : PublicationPolicy.ServiceHistoryVersion, start);
        service.ConfirmPublicSince(start);
        var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 3, 2, start);
        var interval = new HealthStateInterval(target.Id, 1, 1, start);
        interval.Close(now.AddMinutes(-1));
        await using (var db = await normal.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            db.HealthTargets.Add(target); db.HealthStateIntervals.Add(interval);
            await db.SaveChangesAsync(token);
        }
        var gate = new PauseIntervals();
        var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(gate).Options;
        var contexts = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
        var reader = new PublicServiceHistoryReader(contexts, new HealthHistoryReader(contexts));
        async Task<bool> ReadAsync() => periods
            ? await reader.ReadPeriodsAsync(service.Id, start, now.AddMinutes(-1), token) is not null
            : await reader.ReadAsync(service.Id, start, now.AddMinutes(-1), token) is not null;
        var read = ReadAsync();
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            await using (var db = await normal.CreateDbContextAsync(token))
            {
                if (mutation == 1) (await db.Sources.SingleAsync(x => x.Id == source.Id, token)).Revoke(now);
                else if (mutation == 2) (await db.Services.SingleAsync(x => x.Id == service.Id, token)).SetPublication(true, "Orders", PublicationPolicy.ServiceHistoryVersion, now);
                else (await db.Services.SingleAsync(x => x.Id == service.Id, token)).SetPublication(false, null, null, now);
                await db.SaveChangesAsync(token);
            }
            gate.Release.TrySetResult();
            Assert.IsFalse(await read);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            gate.Release.TrySetResult();
            try { await read; } catch when (failure is not null) { }
        }
    }, context.CancellationToken);

    private Task WithHistoryReadMode(bool snapshotReads, Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken cancellationToken) =>
        snapshotReads ? SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await ReadCommittedSnapshot.ConfigureAsync(database, true, token);
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString))
            { await connection.OpenAsync(token); await ReadCommittedSnapshot.EnsureAsync(connection, true, token); }
            await action(database, token);
        }, cancellationToken) : ReusableWebDatabase.ExecuteAsync(action, cancellationToken);

    private sealed class PauseIntervals : DbCommandInterceptor
    {
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[service].[HealthStateIntervals]", StringComparison.Ordinal) && Interlocked.Exchange(ref entered, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Periods_RejectOversizedResultWithoutPartialDisclosure() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var start = DateTimeOffset.UtcNow.AddMinutes(-15);
        var source = new ServiceSource("source", new byte[32], start);
        var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", start);
        var service = new MonitoredService(candidate, start);
        service.SetPublication(true, "Orders", PublicationPolicy.ServiceHistoryDetailsVersion, start);
        service.ConfirmPublicSince(start);
        var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 3, 2, start);
        var intervals = Enumerable.Range(0, 513).Select(i =>
        {
            var interval = new HealthStateInterval(target.Id, (byte)(i % 2 == 0 ? 1 : 2), 1, start.AddSeconds(i));
            interval.Close(start.AddSeconds(i + 1));
            return interval;
        }).ToArray();
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            db.HealthTargets.Add(target); db.HealthStateIntervals.AddRange(intervals);
            await db.SaveChangesAsync(token);
        }
        using var client = PingTestSite.Client(host);
        var range = $"?from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(start.AddSeconds(513).ToString("O"))}";
        using var response = await client.GetAsync($"/api/v1/public/services/{service.Id:D}/history/periods{range}", token);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("periods", body, StringComparison.OrdinalIgnoreCase);
    }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task History_RequiresExplicitPolicyAndClipsPublicationPeriod() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-5);
        var published = now.AddMinutes(-2);
        var end = now.AddMinutes(-1);
        var source = new ServiceSource("private-source", new byte[32], start);
        var candidate = new ServiceCandidate(source.Id, "orders", "private-environment", "Orders", start);
        var service = new MonitoredService(candidate, start);
        service.SetPublication(true, "Orders", PublicationPolicy.ServiceHistoryDetailsVersion, start);
        service.ConfirmPublicSince(published);
        var baselineCandidate = new ServiceCandidate(source.Id, "baseline", "private-environment", "Baseline", start);
        var baseline = new MonitoredService(baselineCandidate, start);
        baseline.SetPublication(true, "Baseline", PublicationPolicy.BaselineVersion, start);
        baseline.ConfirmPublicSince(published);
        var pendingCandidate = new ServiceCandidate(source.Id, "pending", "private-environment", "Pending", start);
        var pending = new MonitoredService(pendingCandidate, start);
        pending.SetPublication(true, "Pending", PublicationPolicy.ServiceHistoryVersion, start);
        var oldCandidate = new ServiceCandidate(source.Id, "old", "private-environment", "Old", start.AddDays(-120));
        var old = new MonitoredService(oldCandidate, start.AddDays(-120));
        old.SetPublication(true, "Old", PublicationPolicy.ServiceHistoryVersion, start.AddDays(-120));
        old.ConfirmPublicSince(start.AddDays(-120));
        var dailyCandidate = new ServiceCandidate(source.Id, "daily", "private-environment", "Daily", start);
        var daily = new MonitoredService(dailyCandidate, start);
        daily.SetPublication(true, "Daily", PublicationPolicy.ServiceHistoryVersion, start);
        daily.ConfirmPublicSince(published);
        var target = new HealthTarget(service.Id, null, "http://127.0.0.1/private-health", 30, 5, 3, 2, start);
        var healthy = new HealthStateInterval(target.Id, 1, 1, start);
        healthy.Close(published.AddSeconds(30));
        var degraded = new HealthStateInterval(target.Id, 2, 1, published.AddSeconds(30));
        degraded.Close(end);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.AddRange(candidate, baselineCandidate, pendingCandidate, oldCandidate, dailyCandidate);
            db.Services.AddRange(service, baseline, pending, old, daily);
            db.HealthTargets.Add(target); db.HealthStateIntervals.AddRange(healthy, degraded);
            await db.SaveChangesAsync(token);
        }
        var group = new Group("External", start);
        group.SetPublication(true, "External", PublicationPolicy.BaselineVersion, start);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            db.Groups.Add(group); db.ServiceGroups.Add(new ServiceGroup(group.Id, service.Id));
            await db.SaveChangesAsync(token);
        }
        using var anonymous = PingTestSite.Client(host);
        var publicGroup = await anonymous.GetFromJsonAsync<PublicGroup>($"/api/v1/public/groups/{group.Id:D}", token);
        Assert.IsNotNull(publicGroup);
        Assert.AreEqual(1, publicGroup.ServiceCount);
        var groupServices = await anonymous.GetFromJsonAsync<PublicPage<PublicService>>($"/api/v1/public/groups/{group.Id:D}/services", token);
        Assert.IsNotNull(groupServices);
        Assert.AreEqual(service.Id, groupServices.Items.Single().Id);
        var range = $"?from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(end.ToString("O"))}";
        using (var denied = await anonymous.GetAsync($"/api/v1/public/services/{baseline.Id:D}/history{range}", token))
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        using (var denied = await anonymous.GetAsync($"/api/v1/public/services/{pending.Id:D}/history{range}", token))
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        using (var denied = await anonymous.GetAsync($"/api/v1/public/services/{baseline.Id:D}/history/periods{range}", token))
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        using (var denied = await anonymous.GetAsync($"/api/v1/public/services/{pending.Id:D}/history/periods{range}", token))
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        using (var dailyResponse = await anonymous.GetAsync($"/api/v1/public/services/{daily.Id:D}/history{range}", token))
            Assert.AreEqual(HttpStatusCode.OK, dailyResponse.StatusCode);
        using (var denied = await anonymous.GetAsync($"/api/v1/public/services/{daily.Id:D}/history/periods{range}", token))
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        using (var tooWide = await anonymous.GetAsync($"/api/v1/public/services/{service.Id:D}/history/periods?from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(2).ToString("O"))}", token))
            Assert.AreEqual(HttpStatusCode.BadRequest, tooWide.StatusCode);
        using (var periodsResponse = await anonymous.GetAsync($"/api/v1/public/services/{service.Id:D}/history/periods{range}", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, periodsResponse.StatusCode);
            Assert.IsTrue(periodsResponse.Headers.CacheControl?.NoStore);
            var json = await periodsResponse.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain("127.0.0.1", json);
            Assert.DoesNotContain("private-source", json);
            Assert.DoesNotContain("private-environment", json);
            Assert.DoesNotContain("unknownParticipants", json);
            var periods = System.Text.Json.JsonSerializer.Deserialize<PublicServiceHealthPeriods>(json, System.Text.Json.JsonSerializerOptions.Web);
            Assert.IsNotNull(periods);
            Assert.HasCount(2, periods.Periods);
            Assert.AreEqual(new PublicHealthPeriod(published, published.AddSeconds(30), "Healthy", false), periods.Periods[0]);
            Assert.AreEqual(new PublicHealthPeriod(published.AddSeconds(30), end, "Degraded", true), periods.Periods[1]);
        }
        using var response = await anonymous.GetAsync($"/api/v1/public/services/{service.Id:D}/history{range}", token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("private-source", body);
        Assert.DoesNotContain("private-environment", body);
        var page = await response.Content.ReadFromJsonAsync<PublicServiceHistory>(cancellationToken: token);
        Assert.IsNotNull(page);
        Assert.IsNotEmpty(page.Days);
        Assert.AreEqual(published, page.Days[0].From);
        Assert.AreEqual(end, page.Days[^1].To);
        Assert.IsTrue(page.Days.All(x => x.Coverage == 1));
        Assert.IsTrue(page.Days.Any(x => x.State == "Degraded"));
        var oldestRange = $"?from={Uri.EscapeDataString(now.AddDays(-91).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}";
        var oldest = await anonymous.GetFromJsonAsync<PublicServiceHistory>($"/api/v1/public/services/{old.Id:D}/history{oldestRange}", token);
        Assert.IsNotNull(oldest);
        Assert.IsNotEmpty(oldest.Days);
        Assert.IsGreaterThanOrEqualTo(now.AddDays(-90), oldest.Days[0].From);
        Assert.IsTrue(oldest.Days.All(x => x.State == "Unknown" && x.Availability is null && x.Coverage == 0));

        var admission = host.Services.GetRequiredService<PublicHistoryAdmission>();
        await admission.Gate.WaitAsync(token);
        await admission.Gate.WaitAsync(token);
        try
        {
            using var busy = await anonymous.GetAsync($"/api/v1/public/services/{service.Id:D}/history{range}", token);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.AreEqual(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter?.Delta);
        }
        finally { admission.Gate.Release(2); }
        using var available = await anonymous.GetAsync($"/api/v1/public/services/{service.Id:D}/history{range}", token);
        Assert.AreEqual(HttpStatusCode.OK, available.StatusCode);
    }, context.CancellationToken);
}
