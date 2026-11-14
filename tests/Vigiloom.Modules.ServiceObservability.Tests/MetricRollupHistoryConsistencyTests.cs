using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricRollupHistoryConsistencyTests(TestContext context)
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, true, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ConcurrentChangeOrRecreationInvalidatesSeriesPage(bool snapshot, bool recreate, bool payload) =>
        Fixtures.ReusableServiceDatabase.ExecuteForReadModeAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-5);
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), start.AddDays(-1));
            var window = new MetricRollupWindow { ServiceId = service.Id, WindowStart = start, InputRevision = 1,
                ProjectedRevision = 1, ProjectionVersion = 1, ProjectedAt = now, IsComplete = true };
            await using (var setup = new ServiceObservabilityDbContext(options))
            {
                setup.Sources.Add(source); setup.Services.Add(service); setup.MetricRollupWindows.Add(window);
                await setup.SaveChangesAsync(token);
                setup.MetricRollupSeries.Add(new MetricRollupSeries { WindowRowId = window.RowId, ServiceId = service.Id,
                    IdentityHash = new byte[32], IdentityBytes = [1], Mode = MetricRollupMode.GaugeSnapshot,
                    ResultVersion = 1, InputCount = 1, ResultPayload = [1] });
                await setup.SaveChangesAsync(token);
            }
            var version = Convert.ToBase64String(window.Version);
            var barrier = new SeriesReadBarrier();
            var readOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(barrier).Options;
            var history = new TelemetryHistory(new PooledDbContextFactory<ServiceObservabilityDbContext>(readOptions));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            async Task<MetricRollupReadStatus> Read()
            {
                if (payload)
                {
                    var result = await history.MetricPayloadAsync(service.Id, start, version, 1, 0, null, cancellation.Token);
                    Assert.IsNull(result.Payload);
                    return result.Status;
                }
                var page = await history.MetricSeriesAsync(service.Id, start, version, 1, null, cancellation.Token);
                Assert.IsNull(page.Page);
                return page.Status;
            }
            var read = Read();
            try
            {
                await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                await using (var change = new ServiceObservabilityDbContext(options))
                await using (var tx = await change.Database.BeginTransactionAsync(token))
                {
                    if (recreate)
                    {
                        await change.MetricRollupWindows.Where(x => x.ServiceId == service.Id && x.WindowStart == start).ExecuteDeleteAsync(token);
                        change.MetricRollupWindows.Add(new MetricRollupWindow { ServiceId = service.Id, WindowStart = start, InputRevision = 1 });
                    }
                    else
                    {
                        var current = await change.MetricRollupWindows.SingleAsync(x => x.ServiceId == service.Id && x.WindowStart == start, token);
                        current.InputRevision++;
                    }
                    await change.SaveChangesAsync(token);
                    await tx.CommitAsync(token);
                }
                barrier.Release.TrySetResult();
                var result = await read.WaitAsync(TimeSpan.FromSeconds(10), token);
                Assert.AreEqual(MetricRollupReadStatus.Changed, result);
            }
            finally
            {
                barrier.Release.TrySetResult(); cancellation.Cancel();
                try { await read; } catch (OperationCanceledException) { }
            }
        }, snapshot, context.CancellationToken);

    private sealed class SeriesReadBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[service].[MetricRollupSeries]", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
