using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Tests.Fixtures;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SpeedRetentionSqlTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Eligibility_UsesExactCutoffAndTwentyFourHourBoundary() => WithDatabase(async (factory, _, token) =>
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-365);
        var protectedSince = now.AddHours(-24);
        var before = Run(cutoff.AddTicks(-1), SpeedTestState.Success);
        var atCutoff = Run(cutoff, SpeedTestState.Success);
        var afterCutoff = Run(cutoff.AddTicks(1), SpeedTestState.Success);
        var atProtectedStart = Run(cutoff.AddDays(-1), SpeedTestState.Success);
        atProtectedStart.StartedAt = protectedSince;
        var afterProtectedStart = Run(cutoff.AddDays(-1), SpeedTestState.Success);
        afterProtectedStart.StartedAt = protectedSince.AddTicks(1);
        var queued = Run(cutoff.AddDays(-1), SpeedTestState.Queued);
        await using var db = await factory.CreateDbContextAsync(token);
        db.SpeedRuns.AddRange(before, atCutoff, afterCutoff, atProtectedStart, afterProtectedStart, queued);
        await db.SaveChangesAsync(token);
        var actual = await SpeedRetentionEligibility.Apply(db.SpeedRuns.AsNoTracking(), cutoff, protectedSince)
            .Select(x => x.Id).ToArrayAsync(token);
        Assert.HasCount(2, actual);
        Assert.Contains(before.Id, actual);
        Assert.Contains(atProtectedStart.Id, actual);
    });

    [TestMethod]
    public Task Cleanup_ConcurrentWorkersKeepActiveAndRecentRunsAndRecordCommittedBatches() => WithDatabase(async (factory, _, token) =>
    {
        var protection = new EphemeralDataProtectionProvider();
        var settings = new SpeedRetentionSettings(factory, new OperationAuditWriter(), protection);
        await settings.GetAsync(token);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            for (var i = 0; i < 501; i++) db.SpeedRuns.Add(Run(now.AddDays(-366), SpeedTestState.Success));
            db.SpeedRuns.Add(Run(now.AddDays(-364), SpeedTestState.Success));
            var recentStart = Run(now.AddDays(-366), SpeedTestState.Success);
            recentStart.StartedAt = now.AddHours(-1);
            db.SpeedRuns.Add(recentStart);
            var queued = Run(now.AddDays(-366), SpeedTestState.Queued); queued.Location = "queue";
            var running = Run(now.AddDays(-366), SpeedTestState.Running); running.Location = "running";
            db.SpeedRuns.AddRange(queued, running);
            await db.SaveChangesAsync(token);
        }

        var results = await Task.WhenAll(new SpeedRetentionCleanup(factory).RunAsync(token), new SpeedRetentionCleanup(factory).RunAsync(token));
        Assert.AreEqual(501, results.Sum());
        var snapshot = await settings.GetAsync(token);
        Assert.AreEqual(501L, snapshot.DeletedTotal);
        Assert.IsNotNull(snapshot.LastBatchAt);
        await using var verify = await factory.CreateDbContextAsync(token);
        Assert.AreEqual(4, await verify.SpeedRuns.CountAsync(token));
        Assert.AreEqual(1, await verify.SpeedRuns.CountAsync(x => x.State == SpeedTestState.Queued, token));
        Assert.AreEqual(1, await verify.SpeedRuns.CountAsync(x => x.State == SpeedTestState.Running, token));
    });

    [TestMethod]
    public Task Cleanup_RespectsOneDayPolicyAndRecentStartProtection() => WithDatabase(async (factory, _, token) =>
    {
        var settings = new SpeedRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider());
        await settings.GetAsync(token);
        var now = DateTimeOffset.UtcNow;
        var due = Run(now.AddHours(-24).AddMinutes(-2), SpeedTestState.Success);
        var recent = Run(now.AddHours(-24).AddMinutes(2), SpeedTestState.Success);
        var recentlyStarted = Run(now.AddDays(-2), SpeedTestState.Success);
        recentlyStarted.StartedAt = now.AddHours(-24).AddMinutes(2);
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            await db.SpeedSettings.ExecuteUpdateAsync(x => x.SetProperty(s => s.RetentionDays, 1), token);
            db.SpeedRuns.AddRange(due, recent, recentlyStarted);
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(1, await new SpeedRetentionCleanup(factory).RunAsync(token));
        await using var verify = await factory.CreateDbContextAsync(token);
        Assert.IsFalse(await verify.SpeedRuns.AnyAsync(x => x.Id == due.Id, token));
        Assert.IsTrue(await verify.SpeedRuns.AnyAsync(x => x.Id == recent.Id, token));
        Assert.IsTrue(await verify.SpeedRuns.AnyAsync(x => x.Id == recentlyStarted.Id, token));
    });

    [TestMethod]
    public Task Cleanup_LimitsEachRunAndPreservesRetainedRows() => WithDatabase(async (factory, _, token) =>
    {
        var settings = new SpeedRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider());
        await settings.GetAsync(token);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            for (var i = 0; i < 5100; i++) db.SpeedRuns.Add(Run(now.AddDays(-366), SpeedTestState.Success));
            for (var i = 0; i < 1000; i++) db.SpeedRuns.Add(Run(now.AddDays(-10), SpeedTestState.Success));
            await db.SaveChangesAsync(token);
        }
        var cleanup = new SpeedRetentionCleanup(factory);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual(5000, await cleanup.RunAsync(token));
        elapsed.Stop();
        TestContext.WriteLine($"First 5,000-row cleanup with 1,000 retained rows: {elapsed.ElapsedMilliseconds} ms.");
        await using (var db = await factory.CreateDbContextAsync(token)) Assert.AreEqual(1100, await db.SpeedRuns.CountAsync(token));
        Assert.AreEqual(100, await cleanup.RunAsync(token));
        await using var verify = await factory.CreateDbContextAsync(token);
        Assert.AreEqual(1000, await verify.SpeedRuns.CountAsync(token));
        Assert.IsFalse(await verify.SpeedRuns.AnyAsync(x => x.RequestedAt < now.AddDays(-365), token));
        Assert.AreEqual(5100L, (await settings.GetAsync(token)).DeletedTotal);
    });

    [TestMethod]
    public Task Cleanup_CancellationKeepsCommittedBatchAndCanResume() => WithDatabase(async (factory, connectionString, token) =>
    {
        var settings = new SpeedRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider());
        await settings.GetAsync(token);
        var expired = DateTimeOffset.UtcNow.AddDays(-366);
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            for (var i = 0; i < 501; i++) db.SpeedRuns.Add(Run(expired, SpeedTestState.Success));
            await db.SaveChangesAsync(token);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var options = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(connectionString)
            .AddInterceptors(new CancelSecondDelete(cancellation)).Options;
        await Assert.ThrowsAsync<OperationCanceledException>(() => new SpeedRetentionCleanup(new Factory(options)).RunAsync(cancellation.Token));
        Assert.AreEqual(500L, (await settings.GetAsync(token)).DeletedTotal);
        Assert.AreEqual(1, await new SpeedRetentionCleanup(factory).RunAsync(token));
        Assert.AreEqual(501L, (await settings.GetAsync(token)).DeletedTotal);
    });

    [TestMethod]
    public Task Cleanup_FailedLaterBatchKeepsCommittedProgressAndCanResume() => WithIsolatedDatabase(async (factory, _, token) =>
    {
        var settings = new SpeedRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider());
        await settings.GetAsync(token);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            for (var i = 0; i < 500; i++) db.SpeedRuns.Add(Run(now.AddDays(-367), SpeedTestState.Success));
            db.SpeedRuns.Add(Run(now.AddDays(-366), SpeedTestState.Success));
            await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER network.SpeedRetentionTestFail ON network.SpeedRuns AFTER DELETE AS
                IF EXISTS (SELECT 1 FROM deleted WHERE DATEDIFF(day, RequestedAt, SYSUTCDATETIME()) < 367)
                    THROW 51000, 'Injected second batch failure', 1;
                """, token);
        }
        await Assert.ThrowsAsync<SqlException>(() => new SpeedRetentionCleanup(factory).RunAsync(token));
        var progress = await settings.GetAsync(token);
        Assert.AreEqual(500L, progress.DeletedTotal);
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            Assert.AreEqual(1, await db.SpeedRuns.CountAsync(token));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER network.SpeedRetentionTestFail", token);
        }
        Assert.AreEqual(1, await new SpeedRetentionCleanup(factory).RunAsync(token));
        Assert.AreEqual(501L, (await settings.GetAsync(token)).DeletedTotal);
    });

    [TestMethod]
    public Task Settings_RequireAuthenticPreviewAndRollbackWhenAuditFails() => WithIsolatedDatabase(async (factory, _, token) =>
    {
        var protection = new EphemeralDataProtectionProvider();
        var settings = new SpeedRetentionSettings(factory, new OperationAuditWriter(), protection);
        var before = await settings.GetAsync(token);
        var actor = Guid.CreateVersion7();
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(30, before.Version, "forged"), actor, token));
        var expiredToken = protection.CreateProtector("Vigiloom.SpeedRetention.Preview.v1").Protect(JsonSerializer.Serialize(new
            { Actor = actor, Days = 30, Version = before.Version, At = DateTimeOffset.UtcNow.AddMinutes(-6) }));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(30, before.Version, expiredToken), actor, token));
        var preview = await settings.PreviewAsync(30, before.Version, actor, token);
        Assert.IsNotNull(preview);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(31, before.Version, preview.ConfirmationToken), actor, token));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(30, before.Version, preview.ConfirmationToken), Guid.CreateVersion7(), token));
        await using (var changed = await factory.CreateDbContextAsync(token))
            await changed.SpeedSettings.ExecuteUpdateAsync(update => update.SetProperty(x => x.RetentionDeletedTotal, 1), token);
        Assert.IsNull(await settings.UpdateAsync(new(30, before.Version, preview.ConfirmationToken), actor, token));
        before = await settings.GetAsync(token);
        preview = await settings.PreviewAsync(30, before.Version, actor, token);
        Assert.IsNotNull(preview);
        await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.ExecuteSqlRawAsync("DROP TABLE operations.AuditEvents", token);
        await Assert.ThrowsAsync<SqlException>(() => settings.UpdateAsync(new(30, before.Version, preview.ConfirmationToken), actor, token));
        var after = await settings.GetAsync(token);
        Assert.AreEqual(before.Days, after.Days);
        Assert.AreEqual(before.Version, after.Version);
    });

    private static SpeedRun Run(DateTimeOffset requestedAt, SpeedTestState state) => new()
    {
        RequestId = Guid.CreateVersion7(), RequestedAt = requestedAt, State = state,
        CompletedAt = state is SpeedTestState.Queued or SpeedTestState.Running ? null : requestedAt.AddMinutes(1)
    };

    private Task WithDatabase(Func<IDbContextFactory<NetworkDbContext>, string, CancellationToken, Task> action) =>
        ReusableNetworkDatabase.ExecuteAsync((database, token) => RunTest(database.ConnectionString, token, false, action), TestContext.CancellationToken);

    private Task WithIsolatedDatabase(Func<IDbContextFactory<NetworkDbContext>, string, CancellationToken, Task> action) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), (database, token) => RunTest(database.ConnectionString, token, true, action), TestContext.CancellationToken);

    private static async Task RunTest(string connectionString, CancellationToken token, bool initializeSchema,
        Func<IDbContextFactory<NetworkDbContext>, string, CancellationToken, Task> action)
    {
        var network = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(connectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        if (initializeSchema)
        {
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(connectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(connectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
            await using (var db = new NetworkDbContext(network)) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
        }
        await action(new Factory(network), connectionString, token);
    }

    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext() => new(options);
    }

    private sealed class CancelSecondDelete(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        private int deletes;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase) && Interlocked.Increment(ref deletes) == 2)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
