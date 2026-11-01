using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SpeedSqlTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public Task ConsentConcurrencyCancellationAndRecovery() => WithDatabase(async (factory, token) =>
    {
        var queue = new SpeedQueue(factory); var settings = new SpeedSettings(factory);
        var actor = Guid.CreateVersion7(); var request = Guid.CreateVersion7();
        Assert.AreEqual(SpeedRequestStatus.ConsentRequired, (await queue.RequestAsync(request, actor, false, token)).Status);
        var initial = await settings.GetAsync(token);
        var saved = await settings.UpdateAsync(new(false, 6, 5, 10, true, initial.Version, initial.ConsentPolicy), actor, token);
        Assert.IsNotNull(saved); Assert.IsTrue(saved.Consent);
        Assert.IsNull(await settings.UpdateAsync(new(false, 6, 5, 10, true, initial.Version, initial.ConsentPolicy), actor, token));
        var competing = await Task.WhenAll(queue.RequestAsync(request, actor, false, token), queue.RequestAsync(request, actor, false, token));
        Assert.AreEqual(competing[0].RunId, competing[1].RunId);
        Assert.AreEqual(SpeedRequestStatus.Busy, (await queue.RequestAsync(Guid.CreateVersion7(), actor, false, token)).Status);
        var claims = await Task.WhenAll(queue.ClaimAsync(token), queue.ClaimAsync(token));
        var lease = claims.Single(x => x is not null)!;
        Assert.IsTrue(await queue.MayContinueAsync(lease, token));
        Assert.IsNotNull(await settings.UpdateAsync(new(false, 6, 5, 10, false, saved.Version), actor, token));
        Assert.IsFalse(await queue.MayContinueAsync(lease, token));
        Assert.IsTrue(await queue.CompleteAsync(lease, new(SpeedTestState.Success, "fixture", DownloadMbps: 10, UploadMbps: 5, Bytes: 1024), token));
        var detail = await new SpeedHistory(factory).GetAsync(lease.Id, token);
        Assert.IsNotNull(detail); Assert.AreEqual(SpeedTestState.Cancelled, detail.Summary.State);
        Assert.IsFalse(await queue.CompleteAsync(lease, new(SpeedTestState.Success), token));
        var current = await settings.GetAsync(token);
        Assert.IsNotNull(await settings.UpdateAsync(new(false, 6, 5, 10, true, current.Version, current.ConsentPolicy), actor, token));
        var next = await queue.RequestAsync(Guid.CreateVersion7(), actor, false, token);
        var abandoned = await queue.ClaimAsync(token); Assert.IsNotNull(abandoned);
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedRuns SET LeaseUntil = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE State=1", token);
        Assert.IsNull(await queue.ClaimAsync(token));
        Assert.IsFalse(await queue.CompleteAsync(abandoned, new(SpeedTestState.Success), token));
        Assert.AreEqual(SpeedTestState.Interrupted, (await new SpeedHistory(factory).GetAsync(next.RunId!.Value, token))!.Summary.State);
    });

    [TestMethod]
    [DataRow(false, 40)]
    [DataRow(true, 4)]
    public Task RollingRateLimitSurvivesNewQueueInstance(bool automatic, int limit) => WithDatabase(async (factory, token) =>
    {
        var actor = Guid.CreateVersion7(); var settings = new SpeedSettings(factory);
        var first = await settings.GetAsync(token);
        await settings.UpdateAsync(new(automatic, 6, 1, 5, true, first.Version, first.ConsentPolicy), actor, token);
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            db.SpeedRuns.AddRange(Enumerable.Range(0, limit).Select(i => new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedBy = automatic ? null : actor,
                RequestedAt = now.AddMinutes(-i), State = SpeedTestState.Failed, CompletedAt = now, Automatic = automatic, MaxMiB = 1, TimeoutSeconds = 5 }));
            await db.SaveChangesAsync(token);
            if (automatic) await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET NextAutomaticAt = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
        }
        var result = await new SpeedQueue(factory).RequestAsync(Guid.CreateVersion7(), automatic ? null : actor, automatic, token);
        Assert.AreEqual(SpeedRequestStatus.RateLimited, result.Status); Assert.IsNotNull(result.NextAllowedAt);
        Assert.IsGreaterThan(now, result.NextAllowedAt.Value);
        await using var check = factory.CreateDbContext(); Assert.AreEqual(limit, await check.SpeedRuns.CountAsync(token));
    });

    [TestMethod]
    [DataRow(false, 40)]
    [DataRow(true, 4)]
    public Task RateWindowUsesActualStartWhenQueueWasDelayed(bool automatic, int limit) => WithDatabase(async (factory, token) =>
    {
        var actor = Guid.CreateVersion7(); var settings = new SpeedSettings(factory);
        var initial = await settings.GetAsync(token);
        await settings.UpdateAsync(new(automatic, 6, 1, 5, true, initial.Version, initial.ConsentPolicy), actor, token);
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            db.SpeedRuns.AddRange(Enumerable.Range(0, limit).Select(i => new SpeedRun { RequestId = Guid.CreateVersion7(),
                RequestedBy = automatic ? null : actor, RequestedAt = now.AddHours(-24).AddMinutes(-1), StartedAt = now.AddHours(-24).AddMinutes(2),
                State = SpeedTestState.Failed, CompletedAt = now.AddHours(-24).AddMinutes(3), Automatic = automatic, MaxMiB = 1, TimeoutSeconds = 5 }));
            await db.SaveChangesAsync(token);
            if (automatic) await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET NextAutomaticAt = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
        }
        var result = await new SpeedQueue(factory).RequestAsync(Guid.CreateVersion7(), automatic ? null : actor, automatic, token);
        Assert.AreEqual(SpeedRequestStatus.RateLimited, result.Status);
        Assert.IsGreaterThan(now, result.NextAllowedAt!.Value);
        var snapshot = await settings.GetAsync(token);
        Assert.AreEqual(limit, snapshot.ReservedToday);
    });

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
                DownloadMbps = 42, Bytes = 1024, Server = "fixture.measurement-lab.org" });
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
    private Task WithDatabase(Func<Factory, CancellationToken, Task> action) => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
        var factory = new Factory(new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options);
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(token);
        await action(factory, token);
    }, TestContext.CancellationToken);
    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext() => new(options);
    }
}
