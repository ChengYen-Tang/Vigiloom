using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HealthHistoryReaderTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task History_StableReadError_PreservesOriginalException() =>
        ExecuteHistoryAsync(false, async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var start = DateTimeOffset.UtcNow.AddMinutes(-3);
            var source = new ServiceSource("source", new byte[32], start);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", start), start);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
            }
            var interceptor = new IntervalReadBarrier(true);
            interceptor.Release.TrySetResult();
            var readOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(interceptor).Options;
            var reader = new HealthHistoryReader(new PooledDbContextFactory<ServiceObservabilityDbContext>(readOptions));
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync(service.Id, start, start.AddMinutes(1), token));
            Assert.AreEqual("Injected interval read failure.", error.Message);
            Assert.AreEqual(1, interceptor.Count);
        });

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task History_ConcurrentStateChange_RetriesConsistentVersion(bool snapshot, bool refresh, bool failFirstRead) =>
        ExecuteHistoryAsync(snapshot, async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var start = DateTimeOffset.UtcNow.AddMinutes(-3);
            var stopped = start.AddSeconds(30);
            var source = new ServiceSource("source", new byte[32], start);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", start), start);
            var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 3600, 5, 3, 2, start);
            target.SetEnabled(true, start);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                if (snapshot) await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON", token);
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); db.HealthTargets.Add(target);
                var schedule = new HealthSchedule(target.Id, true, start);
                schedule.SetEvaluation(1, start, 1, 2, start, start);
                db.HealthSchedules.Add(schedule); db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 1, 1, start));
                var policy = new HealthNetworkPolicy(["127.0.0.0/8"], start);
                db.HealthNetworkPolicies.Add(policy);
                await db.SaveChangesAsync(token);
                schedule.SetVersions(source.Version, service.Version, null, target.Version, policy.Version);
                await db.SaveChangesAsync(token);
            }
            var interceptor = new IntervalReadBarrier(failFirstRead);
            var readOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(interceptor).Options;
            var reader = new HealthHistoryReader(new PooledDbContextFactory<ServiceObservabilityDbContext>(readOptions));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var read = reader.ReadAsync(service.Id, start, start.AddSeconds(60), cancellation.Token);
            try
            {
                await interceptor.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                await using var db = new ServiceObservabilityDbContext(options);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                if (refresh)
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [EvidenceAt] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), [LastEvaluatedAt] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE [TargetId] = {target.Id}", token);
                else
                {
                    await HealthInvalidation.ServiceAsync(db, service.Id, stopped, token);
                    await db.SaveChangesAsync(token);
                }
                await tx.CommitAsync(token);
                interceptor.Release.TrySetResult();
                var result = await read.WaitAsync(TimeSpan.FromSeconds(10), token);
                Assert.IsNotNull(result);
                Assert.HasCount(refresh ? 1 : 2, result.Intervals);
                Assert.AreEqual(ServiceHealthState.Healthy, result.Intervals[0].State);
                if (!refresh)
                {
                    Assert.AreEqual(stopped, result.Intervals[0].End);
                    Assert.AreEqual(ServiceHealthState.Unknown, result.Intervals[1].State);
                }
                Assert.IsGreaterThanOrEqualTo(2, interceptor.Count);
            }
            finally
            {
                interceptor.Release.TrySetResult(); cancellation.Cancel();
                try { await read; } catch (Exception) { }
            }
        });

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task History_MissingParticipantIsUnknown_ExplicitlyDisabledIsExcluded() =>
        ExecuteHistoryAsync(false, async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var start = DateTimeOffset.UtcNow.AddMinutes(-3);
            var source = new ServiceSource("source", new byte[32], start);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", start), start);
            var first = new HealthTarget(service.Id, null, "http://127.0.0.1/a", 30, 5, 3, 2, start);
            var second = new HealthTarget(service.Id, null, "http://127.0.0.1/b", 30, 5, 3, 2, start);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service); db.HealthTargets.AddRange(first, second);
                Add(first.Id, 1, 0, 50);
                Add(second.Id, 1, 0, 10);
                Add(second.Id, 4, 20, 30);
                Add(second.Id, 1, 30, 50);
                await db.SaveChangesAsync(token);
                void Add(Guid id, byte state, int from, int to)
                {
                    var interval = new HealthStateInterval(id, state, 0, start.AddSeconds(from));
                    interval.Close(start.AddSeconds(to));
                    db.HealthStateIntervals.Add(interval);
                }
            }
            var reader = new HealthHistoryReader(new PooledDbContextFactory<ServiceObservabilityDbContext>(options));
            var result = await reader.ReadAsync(service.Id, start, start.AddSeconds(50), token);
            Assert.IsNotNull(result);
            Assert.HasCount(3, result.Intervals);
            Assert.AreEqual(ServiceHealthState.Unknown, result.Intervals[1].State);
            Assert.AreEqual(start.AddSeconds(10), result.Intervals[1].Start);
            Assert.AreEqual(start.AddSeconds(20), result.Intervals[1].End);
            Assert.AreEqual(ServiceHealthState.Healthy, result.Intervals[2].State);
            Assert.AreEqual(0.8, result.Coverage, 0.000001);
        });

    private Task ExecuteHistoryAsync(bool isolated, Func<SqlTestDatabase, CancellationToken, Task> action) =>
        isolated ? SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), action, context.CancellationToken)
                 : ReusableServiceDatabase.ExecuteAsync(action, context.CancellationToken);
}

internal sealed class IntervalReadBarrier(bool failFirst = false) : DbCommandInterceptor
{
    private int count;
    public int Count => Volatile.Read(ref count);
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("[service].[HealthStateIntervals]", StringComparison.Ordinal) && Interlocked.Increment(ref count) == 1)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (failFirst) throw new InvalidOperationException("Injected interval read failure.");
        }
        return result;
    }
}
