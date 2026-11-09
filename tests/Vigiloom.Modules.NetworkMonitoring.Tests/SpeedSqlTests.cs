using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Testing.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SpeedSqlTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task UpgradeTimeout_AdjustsSettingsAndQueuedRunsOnly() => WithDatabase(async (factory, token) =>
    {
        await new SpeedSettings(factory, new OperationAuditWriter()).GetAsync(token);
        var now = DateTimeOffset.UtcNow;
        var queued = new SpeedRun { Location = "queued", RequestId = Guid.CreateVersion7(), RequestedAt = now, TimeoutSeconds = 10, State = SpeedTestState.Queued };
        var running = new SpeedRun { Location = "running", RequestId = Guid.CreateVersion7(), RequestedAt = now, TimeoutSeconds = 10, State = SpeedTestState.Running };
        var completed = new SpeedRun { Location = "completed", RequestId = Guid.CreateVersion7(), RequestedAt = now, TimeoutSeconds = 10, State = SpeedTestState.Success };
        await using var db = factory.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261102023341_ScheduleSpeedTests", token);
        await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET TimeoutSeconds = 10 WHERE Name = N'central'", token);
        db.SpeedRuns.AddRange(queued, running, completed);
        await db.SaveChangesAsync(token);
        await db.GetService<IMigrator>().MigrateAsync(null, token);
        db.ChangeTracker.Clear();
        Assert.AreEqual(40, (await db.SpeedSettings.SingleAsync(x => x.Name == "central", token)).TimeoutSeconds);
        Assert.AreEqual(40, (await db.SpeedRuns.SingleAsync(x => x.Id == queued.Id, token)).TimeoutSeconds);
        Assert.AreEqual(10, (await db.SpeedRuns.SingleAsync(x => x.Id == running.Id, token)).TimeoutSeconds);
        Assert.AreEqual(10, (await db.SpeedRuns.SingleAsync(x => x.Id == completed.Id, token)).TimeoutSeconds);
    });

    [TestMethod]
    public Task UpgradeRetention_PreservesExistingSettingsAndResults() => WithDatabase(async (factory, token) =>
    {
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var initial = await settings.GetAsync(token);
        Assert.IsNotNull(await settings.UpdateAsync(new(false, 12, 100, 45, initial.Version), Guid.CreateVersion7(), token));
        var runId = Guid.CreateVersion7();
        await using var db = factory.CreateDbContext();
        db.SpeedRuns.Add(new SpeedRun { Id = runId, RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow, State = SpeedTestState.Success });
        await db.SaveChangesAsync(token);
        await db.GetService<IMigrator>().MigrateAsync("20261102220000_SpeedTimeoutMinimum", token);
        await db.GetService<IMigrator>().MigrateAsync(null, token);
        db.ChangeTracker.Clear();
        var actual = await db.SpeedSettings.SingleAsync(x => x.Name == "central", token);
        Assert.AreEqual(365, actual.RetentionDays);
        Assert.AreEqual(0L, actual.RetentionDeletedTotal);
        Assert.IsFalse(actual.Automatic);
        Assert.AreEqual(12, actual.IntervalHours);
        Assert.IsTrue(await db.SpeedRuns.AnyAsync(x => x.Id == runId, token));
    });

    [TestMethod]
    public Task UpgradeSchedule_PreservesCustomSettingsAndHistory() => WithDatabase(async (factory, token) =>
    {
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        await settings.GetAsync(token);
        var runId = Guid.CreateVersion7();
        await using (var db = factory.CreateDbContext())
        {
            db.SpeedRuns.Add(new SpeedRun { Id = runId, RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow, State = SpeedTestState.NoServer });
            await db.SaveChangesAsync(token);
            await db.GetService<IMigrator>().MigrateAsync("20261101172257_SpeedTests", token);
            await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET Automatic=0, MaxMiB=200, UpdatedBy=NULL, ConsentPolicy=NULL, NextAutomaticAt=NULL WHERE Name=N'central'", token);
            await db.GetService<IMigrator>().MigrateAsync(null, token);
        }
        var upgraded = await settings.GetAsync(token);
        Assert.IsTrue(upgraded.Automatic);
        Assert.AreEqual(2048, upgraded.MaxMiB);
        Assert.IsNotNull(upgraded.NextAutomaticAt);
        Assert.IsNotNull(await new SpeedHistory(factory).GetAsync(runId, token));
        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261101172257_SpeedTests", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE network.SpeedSettings SET Automatic=0, MaxMiB=123, UpdatedBy={Guid.CreateVersion7()}, NextAutomaticAt=NULL WHERE Name=N'central'", token);
            await db.GetService<IMigrator>().MigrateAsync(null, token);
        }
        var customized = await settings.GetAsync(token);
        Assert.IsFalse(customized.Automatic);
        Assert.AreEqual(123, customized.MaxMiB);
        Assert.IsNull(customized.NextAutomaticAt);
        Assert.IsNotNull(await new SpeedHistory(factory).GetAsync(runId, token));
    });
    [TestMethod]
    public Task PooledContexts_CanWriteConcurrentlyWithoutSharingState() => WithDatabase(async (factory, token) =>
    {
        string connection;
        await using (var context = factory.CreateDbContext()) connection = context.Database.GetDbConnection().ConnectionString;
        var services = new ServiceCollection();
        services.AddModuleSqlServer<NetworkDbContext>(connection, "network", pooled: true);
        using var provider = services.BuildServiceProvider();
        var pooled = provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        await using (var first = await pooled.CreateDbContextAsync(token))
        await using (var second = await pooled.CreateDbContextAsync(token))
        {
            Assert.AreNotSame(first, second);
            first.SpeedRuns.Add(new SpeedRun { Id = firstId, RequestId = Guid.CreateVersion7(), RequestedAt = now, State = SpeedTestState.NoServer });
            second.SpeedRuns.Add(new SpeedRun { Id = secondId, RequestId = Guid.CreateVersion7(), RequestedAt = now, State = SpeedTestState.NoServer });
            await Task.WhenAll(first.SaveChangesAsync(token), second.SaveChangesAsync(token));
        }
        await using var check = await pooled.CreateDbContextAsync(token);
        Assert.IsEmpty(check.ChangeTracker.Entries());
        Assert.AreEqual(2, await check.SpeedRuns.CountAsync(x => x.Id == firstId || x.Id == secondId, token));
    });

    [TestMethod]
    public Task ScheduleConcurrencyCancellationAndRecovery() => WithDatabase(async (factory, token) =>
    {
        var queue = new SpeedQueue(factory); var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var actor = Guid.CreateVersion7(); var request = Guid.CreateVersion7();
        var initial = await settings.GetAsync(token);
        var saved = await settings.UpdateAsync(new(true, 6, 5, 40, initial.Version), actor, token);
        Assert.IsNotNull(saved);
        Assert.IsNull(await settings.UpdateAsync(new(true, 6, 5, 40, initial.Version), actor, token));
        await SetAutomaticDueAsync(factory, token);
        var competing = await Task.WhenAll(queue.RequestAsync(request, token), queue.RequestAsync(request, token));
        Assert.AreEqual(competing[0].RunId, competing[1].RunId);
        await SetAutomaticDueAsync(factory, token);
        Assert.AreEqual(SpeedRequestStatus.Busy, (await queue.RequestAsync(Guid.CreateVersion7(), token)).Status);
        var claims = await Task.WhenAll(queue.ClaimAsync(token), queue.ClaimAsync(token));
        var lease = claims.Single(x => x is not null)!;
        Assert.IsTrue(await queue.MayContinueAsync(lease, token));
        var beforeDisable = await settings.GetAsync(token);
        Assert.IsNotNull(await settings.UpdateAsync(new(false, 6, 5, 40, beforeDisable.Version), actor, token));
        Assert.IsFalse(await queue.MayContinueAsync(lease, token));
        Assert.IsTrue(await queue.CompleteAsync(lease, new(SpeedTestState.Success, "fixture", DownloadMbps: 10, UploadMbps: 5, Bytes: 1024), token));
        var detail = await new SpeedHistory(factory).GetAsync(lease.Id, token);
        Assert.IsNotNull(detail); Assert.AreEqual(SpeedTestState.Cancelled, detail.Summary.State);
        Assert.IsFalse(await queue.CompleteAsync(lease, new(SpeedTestState.Success), token));
        var current = await settings.GetAsync(token);
        Assert.IsNotNull(await settings.UpdateAsync(new(true, 6, 5, 40, current.Version), actor, token));
        await SetAutomaticDueAsync(factory, token);
        var next = await queue.RequestAsync(Guid.CreateVersion7(), token);
        var abandoned = await queue.ClaimAsync(token); Assert.IsNotNull(abandoned);
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedRuns SET LeaseUntil = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE State=1", token);
        Assert.IsNull(await queue.ClaimAsync(token));
        Assert.IsFalse(await queue.CompleteAsync(abandoned, new(SpeedTestState.Success), token));
        Assert.AreEqual(SpeedTestState.Interrupted, (await new SpeedHistory(factory).GetAsync(next.RunId!.Value, token))!.Summary.State);
    });

    [TestMethod]
    public Task AutomaticSchedule_ClaimsOnceAndStopsWhenDisabled() => WithDatabase(async (factory, token) =>
    {
        var queue = new SpeedQueue(factory);
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var actor = Guid.CreateVersion7();
        var initial = await settings.GetAsync(token);
        Assert.IsTrue(initial.Automatic);
        var enabled = await settings.UpdateAsync(new(true, 6, 100, 40, initial.Version), actor, token);
        Assert.IsNotNull(enabled);
        Assert.IsNotNull(enabled.NextAutomaticAt);
        Assert.IsGreaterThan(DateTimeOffset.UtcNow.AddHours(4), enabled.NextAutomaticAt.Value);
        Assert.IsLessThan(DateTimeOffset.UtcNow.AddHours(8), enabled.NextAutomaticAt.Value);
        await SetAutomaticDueAsync(factory, token);
        var requests = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => queue.RequestAsync(Guid.CreateVersion7(), token)));
        var accepted = requests.Single(x => x.Status == SpeedRequestStatus.Accepted);
        Assert.AreEqual(3, requests.Count(x => x.Status == SpeedRequestStatus.NotDue));
        Assert.IsNotNull(accepted.RunId);
        await SetAutomaticDueAsync(factory, token);
        Assert.AreEqual(SpeedRequestStatus.Busy, (await queue.RequestAsync(Guid.CreateVersion7(), token)).Status);
        var queued = await new SpeedHistory(factory).GetAsync(accepted.RunId.Value, token);
        Assert.IsNotNull(queued);
        Assert.IsTrue(queued.Summary.Automatic);
        Assert.AreEqual("central", queued.Summary.Location);
        Assert.AreEqual(100, queued.MaxMiB);
        Assert.AreEqual(40, queued.TimeoutSeconds);

        var current = await settings.GetAsync(token);
        var disabled = await settings.UpdateAsync(new(false, 6, 100, 40, current.Version), actor, token);
        Assert.IsNotNull(disabled);
        Assert.IsNull(disabled.NextAutomaticAt);
        Assert.AreEqual(SpeedTestState.Cancelled, (await new SpeedHistory(factory).GetAsync(accepted.RunId.Value, token))!.Summary.State);
        Assert.IsNull(await queue.ClaimAsync(token));

        var reenabled = await settings.UpdateAsync(new(true, 6, 100, 40, disabled.Version), actor, token);
        Assert.IsNotNull(reenabled);
        await SetAutomaticDueAsync(factory, token);
        var running = await queue.RequestAsync(Guid.CreateVersion7(), token);
        Assert.AreEqual(SpeedRequestStatus.Accepted, running.Status);
        var lease = await queue.ClaimAsync(token);
        Assert.IsNotNull(lease);
        Assert.IsTrue(await queue.MayContinueAsync(lease, token));
        current = await settings.GetAsync(token);
        Assert.IsNotNull(await settings.UpdateAsync(new(false, 6, 100, 40, current.Version), actor, token));
        Assert.IsFalse(await queue.MayContinueAsync(lease, token));
        Assert.IsTrue(await queue.CompleteAsync(lease, new(SpeedTestState.Success, DownloadMbps: 10, UploadMbps: 5), token));
        Assert.AreEqual(SpeedTestState.Cancelled, (await new SpeedHistory(factory).GetAsync(lease.Id, token))!.Summary.State);
    });

    private static async Task SetAutomaticDueAsync(Factory factory, CancellationToken token)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET NextAutomaticAt = DATEADD(minute,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE Name=N'central'", token);
    }

    [TestMethod]
    public Task HistoryKeepsPartialValuesAndMissingMeasurementsDistinct() => WithDatabase(async (factory, token) =>
    {
        var now = DateTimeOffset.UtcNow; Guid failed;
        await using (var db = factory.CreateDbContext())
        {
            var noData = new SpeedRun { RequestId = Guid.CreateVersion7(), State = SpeedTestState.NoServer, RequestedAt = now, CompletedAt = now };
            failed = noData.Id;
            db.SpeedRuns.Add(noData);
            db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), State = SpeedTestState.BudgetExceeded, RequestedAt = now.AddSeconds(1), CompletedAt = now,
                DownloadMbps = 42, Bytes = 1024, Server = "speedtest.example.com" });
            await db.SaveChangesAsync(token);
        }
        var history = new SpeedHistory(factory);
        var page = await history.QueryAsync(new(now.AddDays(-1), now.AddDays(1)), token);
        Assert.HasCount(2, page.Items); Assert.HasCount(2, page.Trend);
        var detail = await history.GetAsync(failed, token); Assert.IsNotNull(detail);
        Assert.IsNull(detail.Summary.DownloadMbps); Assert.IsNull(detail.Summary.Bytes);
        Assert.IsNull(await history.GetAsync(Guid.CreateVersion7(), token));
        await Assert.ThrowsAsync<ArgumentException>(() => history.QueryAsync(new(now.AddDays(-90), now), token));
    });

    [TestMethod]
    public Task History_FiltersSortsAndPagesConsistently() => WithDatabase(async (factory, token) =>
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = factory.CreateDbContext())
        {
            for (var i = 1; i <= 51; i++)
                db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), State = SpeedTestState.Success,
                    RequestedAt = start.AddMinutes(i), Server = "Taipei test server", DownloadMbps = i, UploadMbps = 100 - i });
            db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), State = SpeedTestState.Success,
                RequestedAt = start.AddMinutes(52), Server = "Other server", DownloadMbps = 1000 });
            await db.SaveChangesAsync(token);
        }

        var history = new SpeedHistory(factory);
        var query = new SpeedHistoryQuery(start, start.AddDays(1), "Taipei", "download", true);
        var first = await history.QueryAsync(query, token);
        var second = await history.QueryAsync(query with { Page = 2 }, token);
        Assert.AreEqual(51, first.Total);
        Assert.HasCount(50, first.Items);
        Assert.HasCount(1, second.Items);
        Assert.AreEqual(51d, first.Items[0].DownloadMbps);
        Assert.AreEqual(2d, first.Items[^1].DownloadMbps);
        Assert.AreEqual(1d, second.Items[0].DownloadMbps);
        Assert.HasCount(51, first.Trend);
        Assert.AreEqual(1d, first.Trend[0].DownloadMbps);
        Assert.AreEqual(51d, first.Trend[^1].DownloadMbps);
        Assert.IsNotNull(await history.GetAsync(second.Items[0].Id, token));
        var ascending = await history.QueryAsync(query with { Sort = "upload", Descending = false }, token);
        Assert.AreEqual(49d, ascending.Items[0].UploadMbps);
        Assert.AreEqual(98d, ascending.Items[^1].UploadMbps);
    });

    [TestMethod]
    public Task Runner_PersistsFailedAndTimedOutRunsWithoutZeroSpeeds() => WithDatabase(async (factory, token) =>
    {
        var queue = new SpeedQueue(factory);
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var actor = Guid.CreateVersion7();
        var initial = await settings.GetAsync(token);
        Assert.IsNotNull(await settings.UpdateAsync(new(true, 6, 5, 40, initial.Version), actor, token));
        foreach (var (state, code) in new[] { (SpeedTestState.NoServer, "no_valid_servers"), (SpeedTestState.TimedOut, "timeout"),
            (SpeedTestState.Failed, "provider_error"), (SpeedTestState.Cancelled, "cancelled") })
        {
            await SetAutomaticDueAsync(factory, token);
            var request = await queue.RequestAsync(Guid.CreateVersion7(), token);
            Assert.AreEqual(SpeedRequestStatus.Accepted, request.Status);
            Assert.IsNotNull(request.RunId);
            var runner = new SpeedRunner(queue, new FixedProbe(new(state, DiagnosticCode: code)), NullLogger<SpeedRunner>.Instance);
            await runner.RunOnceAsync(token);
            var detail = await new SpeedHistory(factory).GetAsync(request.RunId.Value, token);
            Assert.IsNotNull(detail);
            Assert.AreEqual(state, detail.Summary.State);
            Assert.AreEqual(code, detail.DiagnosticCode);
            Assert.AreEqual("central", detail.Summary.Location);
            Assert.IsNotNull(detail.Summary.CompletedAt);
            Assert.IsNull(detail.Summary.DownloadMbps);
            Assert.IsNull(detail.Summary.UploadMbps);
        }
        await SetAutomaticDueAsync(factory, token);
        var active = await queue.RequestAsync(Guid.CreateVersion7(), token);
        Assert.IsNotNull(active.RunId);
        var waiting = new WaitingProbe();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var activeRun = new SpeedRunner(queue, waiting, NullLogger<SpeedRunner>.Instance).RunOnceAsync(stop.Token);
        try
        {
            await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.IsTrue(await queue.CancelAsync(active.RunId.Value, token));
            await activeRun.WaitAsync(TimeSpan.FromSeconds(5), token);
            var cancelled = await new SpeedHistory(factory).GetAsync(active.RunId.Value, token);
            Assert.IsNotNull(cancelled);
            Assert.AreEqual(SpeedTestState.Cancelled, cancelled.Summary.State);
            Assert.AreEqual("cancelled", cancelled.DiagnosticCode);
            Assert.AreEqual(8d, cancelled.Summary.DownloadMbps);
            Assert.IsNull(cancelled.Summary.UploadMbps);
        }
        finally
        {
            await stop.CancelAsync();
            try { await activeRun.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    });

    [TestMethod]
    [DoNotParallelize]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Runner_RealClientPersistsMeasurementAndHistory() => WithDatabase(async (factory, token) =>
    {
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var queue = new SpeedQueue(factory);
        var actor = Guid.CreateVersion7();
        var initial = await settings.GetAsync(token);
        Assert.IsNotNull(await settings.UpdateAsync(new(true, 6, 200, 60, initial.Version), actor, token));
        await SetAutomaticDueAsync(factory, token);
        var request = await queue.RequestAsync(Guid.CreateVersion7(), token);
        Assert.AreEqual(SpeedRequestStatus.Accepted, request.Status);
        Assert.IsNotNull(request.RunId);

        var handler = new ControlledPhaseHandler();
        var probe = new SpeedtestNetProbe(new ControlledPhaseFactory(handler), 2000, 4);
        var runner = new SpeedRunner(queue, probe, NullLogger<SpeedRunner>.Instance);
        await runner.RunOnceAsync(token);

        var history = new SpeedHistory(factory);
        var detail = await history.GetAsync(request.RunId.Value, token);
        Assert.IsNotNull(detail);
        Assert.AreEqual(SpeedTestState.Success, detail.Summary.State);
        Assert.AreEqual("central", detail.Summary.Location);
        Assert.AreEqual("Example (#1) · speedtest.example.com", detail.Summary.Server);
        Assert.AreEqual("Taipei", detail.City);
        Assert.IsNotNull(detail.Summary.StartedAt);
        Assert.IsNotNull(detail.Summary.CompletedAt);
        Assert.IsNotNull(detail.Summary.DownloadMbps);
        Assert.IsGreaterThan(0d, detail.Summary.DownloadMbps.Value);
        Assert.IsNotNull(detail.Summary.UploadMbps);
        Assert.IsGreaterThan(0d, detail.Summary.UploadMbps.Value);
        Assert.IsNotNull(detail.Summary.Bytes);
        Assert.IsGreaterThan(0L, detail.Summary.Bytes.Value);
        Assert.IsNull(detail.Isp);
        var page = await history.QueryAsync(new(detail.Summary.RequestedAt.AddMinutes(-1), detail.Summary.RequestedAt.AddMinutes(1)), token);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual(detail.Summary.Id, page.Items[0].Id);
        Assert.AreEqual(detail.Summary.DownloadMbps, page.Items[0].DownloadMbps);
        Assert.AreEqual(detail.Summary.UploadMbps, page.Items[0].UploadMbps);
        Assert.HasCount(1, page.Trend);
        Assert.AreEqual(detail.Summary.Id, page.Trend[0].Id);

        var cleanup = new SpeedRetentionCleanup(factory);
        Assert.AreEqual(0, await cleanup.RunAsync(token), "Freshly produced results remain within retention.");
        Assert.IsNotNull(await history.GetAsync(request.RunId.Value, token));
        // The result above was produced by the actual runner and controlled provider. Move its
        // stored timestamps across the retention boundary rather than waiting a year.
        var expired = DateTimeOffset.UtcNow.AddDays(-366);
        await using (var db = factory.CreateDbContext())
            await db.SpeedRuns.Where(x => x.Id == request.RunId.Value).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.RequestedAt, expired).SetProperty(x => x.StartedAt, expired)
                .SetProperty(x => x.CompletedAt, expired.AddMinutes(1)), token);
        Assert.AreEqual(1, await cleanup.RunAsync(token));
        Assert.IsNull(await history.GetAsync(request.RunId.Value, token));
        var expiredPage = await history.QueryAsync(new(expired.AddMinutes(-1), expired.AddMinutes(1)), token);
        Assert.IsEmpty(expiredPage.Items);
    });

    private sealed class FixedProbe(SpeedMeasurement result) : ISpeedProbe
    {
        public Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token) => Task.FromResult(result);
    }
    private sealed class WaitingProbe : ISpeedProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(SpeedTestState.Cancelled, DownloadMbps: 8, DiagnosticCode: "cancelled");
            }
            throw new InvalidOperationException("The probe should only stop through cancellation.");
        }
    }
    private Task WithDatabase(Func<Factory, CancellationToken, Task> action) => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
        var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
        var factory = new Factory(new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options);
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(token);
        await action(factory, token);
    }, TestContext.CancellationToken);
    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext() => new(options);
    }
}
