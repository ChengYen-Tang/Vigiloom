using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HealthRunnerTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task RunOnce_ProbesAllowedEndpointAndSavesObservation() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            Task? server = null;
            try
            {
                var options = Options(database.ConnectionString);
                var targetId = await SeedAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                server = ServeAsync(listener, null, null, cancellation.Token);
                using var runner = new HealthRunner(new PooledDbContextFactory<ServiceObservabilityDbContext>(options), TimeProvider.System);
                await runner.RunOnceAsync(cancellation.Token);
                await server;
                await using var db = new ServiceObservabilityDbContext(options);
                var observation = await db.HealthObservations.SingleAsync(token);
                Assert.AreEqual(targetId, observation.TargetId);
                Assert.AreEqual(HealthObservationKind.Healthy, observation.Kind);
                Assert.AreEqual(1L, observation.NetworkGeneration);
                var target = await db.HealthTargets.SingleAsync(token);
                var latest = await new HealthObservationReader(new PooledDbContextFactory<ServiceObservabilityDbContext>(options)).LatestAsync(target.ServiceId, 1, token);
                Assert.HasCount(1, latest);
                Assert.AreEqual(targetId, latest[0].TargetId);
                Assert.AreEqual(Vigiloom.Contracts.Services.HealthCheckObservationKind.Healthy, latest[0].Kind);
                var schedule = await db.HealthSchedules.SingleAsync(token);
                Assert.IsNull(schedule.LeaseOwner);
                Assert.IsNull(schedule.LeaseUntil);
                Assert.IsGreaterThan(observation.ReceivedAt, schedule.DueAt);
            }
            finally
            {
                cancellation.Cancel(); listener.Stop();
                if (server is not null) try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task TwoRecordedHealthyChecks_CreateARealHealthyInterval() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            Task? server = null;
            try
            {
                var options = Options(database.ConnectionString);
                var targetId = await SeedAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                using var runner = new HealthRunner(new PooledDbContextFactory<ServiceObservabilityDbContext>(options), TimeProvider.System);
                server = ServeAsync(listener, null, null, cancellation.Token);
                await runner.RunOnceAsync(cancellation.Token);
                await server;
                await using (var db = new ServiceObservabilityDbContext(options))
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [DueAt] = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [TargetId] = {targetId}", token);
                server = ServeAsync(listener, null, null, cancellation.Token);
                await runner.RunOnceAsync(cancellation.Token);
                await server;
                await using var verify = new ServiceObservabilityDbContext(options);
                var intervals = await verify.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
                Assert.HasCount(2, intervals);
                Assert.AreEqual((byte)EvaluatedHealth.Unknown, intervals[0].State);
                Assert.AreEqual((byte)EvaluatedHealth.Healthy, intervals[1].State);
                Assert.AreEqual(intervals[0].EndedAt, intervals[1].StartedAt);
                Assert.IsNull(intervals[1].EndedAt);
                Assert.AreEqual(2, await verify.HealthObservations.CountAsync(token));
                Assert.AreEqual((byte)EvaluatedHealth.Healthy, (await verify.HealthSchedules.SingleAsync(token)).State);
                var serviceId = (await verify.HealthTargets.SingleAsync(token)).ServiceId;
                var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
                var status = new HealthStatusReader(factory);
                Assert.AreEqual(new Vigiloom.Contracts.Services.ServiceHealthStatus(Vigiloom.Contracts.Services.ServiceHealthState.Healthy, 1, 0), await status.ReadAsync(serviceId, token));
                var history = new HealthHistoryReader(factory);
                var end = await new HealthStore(factory).NowAsync(token);
                var historical = await history.ReadAsync(serviceId, intervals[0].StartedAt, end, token);
                Assert.IsNotNull(historical);
                Assert.AreEqual(Vigiloom.Contracts.Services.ServiceHealthState.Healthy, historical.Intervals[^1].State);
                Assert.IsGreaterThan(0, historical.Coverage);
                var futureEvidence = DateTimeOffset.UtcNow.AddMinutes(5);
                await verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [EvidenceAt] = {futureEvidence}, [LastEvaluatedAt] = {futureEvidence} WHERE [TargetId] = {targetId}", token);
                Assert.AreEqual(new Vigiloom.Contracts.Services.ServiceHealthStatus(Vigiloom.Contracts.Services.ServiceHealthState.Unknown, 1, 1), await status.ReadAsync(serviceId, token));
                historical = await history.ReadAsync(serviceId, intervals[0].StartedAt, end, token);
                Assert.IsNotNull(historical);
                Assert.AreEqual(Vigiloom.Contracts.Services.ServiceHealthState.Unknown, historical.Intervals[^1].State);
                var oldEvidence = DateTimeOffset.UtcNow.AddSeconds(-70);
                await verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [EvidenceAt] = {oldEvidence}, [LastEvaluatedAt] = {oldEvidence} WHERE [TargetId] = {targetId}", token);
                Assert.AreEqual(new Vigiloom.Contracts.Services.ServiceHealthStatus(Vigiloom.Contracts.Services.ServiceHealthState.Unknown, 1, 1), await status.ReadAsync(serviceId, token));
            }
            finally
            {
                cancellation.Cancel(); listener.Stop();
                if (server is not null) try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ServiceResumed_RequiresNewRecoveryEvidence() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            Task? server = null;
            try
            {
                var options = Options(database.ConnectionString);
                var targetId = await SeedAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                using var runner = new HealthRunner(new PooledDbContextFactory<ServiceObservabilityDbContext>(options), TimeProvider.System);
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt == 2)
                    {
                        await using var db = new ServiceObservabilityDbContext(options);
                        await using var tx = await db.Database.BeginTransactionAsync(token);
                        var service = await db.Services.SingleAsync(token);
                        var stoppedAt = await DiscoveryTransaction.NowAsync(db, token);
                        await HealthInvalidation.ServiceAsync(db, service.Id, stoppedAt, token);
                        service.Revoke(stoppedAt);
                        await db.SaveChangesAsync(token);
                        await tx.CommitAsync(token);
                        var stoppedIntervals = await db.HealthStateIntervals.AsNoTracking().OrderBy(x => x.RowId).ToArrayAsync(token);
                        Assert.AreEqual(stoppedAt, stoppedIntervals[1].EndedAt);
                        Assert.AreEqual(stoppedAt, stoppedIntervals[2].StartedAt);
                        Assert.IsTrue(stoppedIntervals[1].StartedAt < stoppedAt);
                        service.Resume(DateTimeOffset.UtcNow);
                        await db.SaveChangesAsync(token);
                    }
                    await using (var db = new ServiceObservabilityDbContext(options))
                        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [DueAt] = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [TargetId] = {targetId}", token);
                    server = ServeAsync(listener, null, null, cancellation.Token);
                    await runner.RunOnceAsync(cancellation.Token);
                    await server;
                    await using var verify = new ServiceObservabilityDbContext(options);
                    var schedule = await verify.HealthSchedules.SingleAsync(token);
                    if (attempt == 1) Assert.AreEqual((byte)EvaluatedHealth.Healthy, schedule.State);
                    if (attempt == 2)
                    {
                        Assert.AreEqual((byte)EvaluatedHealth.Unknown, schedule.State);
                        Assert.AreEqual(1, schedule.StreakCount);
                    }
                    if (attempt == 3) Assert.AreEqual((byte)EvaluatedHealth.Healthy, schedule.State);
                }
                await using var final = new ServiceObservabilityDbContext(options);
                var intervals = await final.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
                Assert.HasCount(4, intervals);
                Assert.AreEqual((byte)EvaluatedHealth.Unknown, intervals[2].State);
                Assert.AreEqual((byte)EvaluatedHealth.Healthy, intervals[3].State);
                DateTimeOffset policyChangedAt;
                await using (var db = new ServiceObservabilityDbContext(options))
                {
                    policyChangedAt = await DiscoveryTransaction.NowAsync(db, token);
                    var policy = await db.HealthNetworkPolicies.SingleAsync(token);
                    policy.ReplaceNetworks(["127.0.0.0/8"], policyChangedAt);
                    db.HealthNetworkRevisions.Add(new HealthNetworkRevision(policy, Guid.CreateVersion7(), policyChangedAt));
                    await db.SaveChangesAsync(token);
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [DueAt] = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [TargetId] = {targetId}", token);
                }
                server = ServeAsync(listener, null, null, cancellation.Token);
                await runner.RunOnceAsync(cancellation.Token);
                await server;
                await using var afterPolicy = new ServiceObservabilityDbContext(options);
                var changed = await afterPolicy.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
                Assert.HasCount(5, changed);
                Assert.AreEqual(policyChangedAt, changed[3].EndedAt);
                Assert.AreEqual(policyChangedAt, changed[4].StartedAt);
                Assert.AreEqual((byte)EvaluatedHealth.Unknown, (await afterPolicy.HealthSchedules.SingleAsync(token)).State);
            }
            finally
            {
                cancellation.Cancel(); listener.Stop();
                if (server is not null) try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task DelayedObservation_IsExpiredAtItsEvidenceDeadline() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = Options(database.ConnectionString);
            var targetId = await SeedAsync(options, 12345, token);
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var store = new HealthStore(factory);
            var observedAt = (await store.NowAsync(token)).AddSeconds(-100);
            await using (var db = new ServiceObservabilityDbContext(options))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthStateIntervals] SET [StartedAt] = {observedAt.AddSeconds(-10)} WHERE [TargetId] = {targetId}", token);
            var lease = Assert.ContainsSingle(await store.ClaimAsync(1, token));
            var work = await store.LoadAsync(lease, token);
            var network = await store.NetworkAsync(token);
            Assert.IsNotNull(work);
            Assert.IsNotNull(network);
            Assert.IsTrue(await store.CommitAsync(work, network, HealthObservationKind.Degraded, observedAt, token));
            await using var verify = new ServiceObservabilityDbContext(options);
            var intervals = await verify.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(3, intervals);
            Assert.AreEqual((byte)EvaluatedHealth.Degraded, intervals[1].State);
            Assert.AreEqual(observedAt, intervals[1].StartedAt);
            Assert.AreEqual(observedAt.AddSeconds(65), intervals[1].EndedAt);
            Assert.AreEqual((byte)EvaluatedHealth.Unknown, intervals[2].State);
            Assert.AreEqual((byte)EvaluatedHealth.Unknown, (await verify.HealthSchedules.SingleAsync(token)).State);
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PolicyChangeBeforeExpiry_EndsHistoryAtPolicyBoundary(bool invalidateService) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = Options(database.ConnectionString);
            var targetId = await SeedAsync(options, 12345, token);
            var observed = DateTimeOffset.UtcNow.AddSeconds(-100);
            var changed = observed.AddSeconds(10);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var source = await db.Sources.SingleAsync(token);
                var service = await db.Services.SingleAsync(token);
                var target = await db.HealthTargets.SingleAsync(token);
                var policy = await db.HealthNetworkPolicies.SingleAsync(token);
                var schedule = await db.HealthSchedules.SingleAsync(token);
                schedule.SetEvaluation(1, observed, 1, 2, observed, observed);
                schedule.SetVersions(source.Version, service.Version, null, target.Version, policy.Version);
                db.HealthObservations.Add(new HealthObservation(targetId, 1, policy.Generation, source.Version, service.Version,
                    null, target.Version, policy.Version, HealthObservationKind.Healthy, observed, observed));
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthStateIntervals] SET [State] = 1, [Reason] = 1, [StartedAt] = {observed} WHERE [TargetId] = {targetId}", token);
                policy.ReplaceNetworks([], changed);
                db.HealthNetworkRevisions.Add(new HealthNetworkRevision(policy, Guid.CreateVersion7(), changed));
                await db.SaveChangesAsync(token);
            }
            if (invalidateService)
            {
                await using var db = new ServiceObservabilityDbContext(options);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var service = await db.Services.SingleAsync(token);
                await HealthInvalidation.ServiceAsync(db, service.Id, await DiscoveryTransaction.NowAsync(db, token), token);
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            else
            {
                using var runner = new HealthRunner(new PooledDbContextFactory<ServiceObservabilityDbContext>(options), TimeProvider.System);
                await runner.ExpireStaleAsync(token);
            }
            await using var verify = new ServiceObservabilityDbContext(options);
            var intervals = await verify.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(2, intervals);
            Assert.AreEqual(changed, intervals[0].EndedAt);
            Assert.AreEqual(changed, intervals[1].StartedAt);
            Assert.AreEqual((byte)EvaluatedHealth.Unknown, intervals[1].State);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ExpiryWithoutNewObservation_ClosesAtEvidenceDeadlineOnce() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = Options(database.ConnectionString);
            var targetId = await SeedAsync(options, 12345, token);
            var evidence = DateTimeOffset.UtcNow.AddSeconds(-70);
            var start = evidence.AddSeconds(-30);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthSchedules] SET [State] = 1, [EvidenceAt] = {evidence}, [Streak] = 1, [StreakCount] = 2, [StreakAt] = {evidence}, [LastEvaluatedAt] = {evidence}, [NetworkVersion] = (SELECT [Version] FROM [service].[HealthNetworkPolicies] WHERE [Name] = 'central') WHERE [TargetId] = {targetId}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthStateIntervals] SET [State] = 1, [Reason] = 1, [StartedAt] = {start} WHERE [TargetId] = {targetId}", token);
            }
            using var runner = new HealthRunner(new PooledDbContextFactory<ServiceObservabilityDbContext>(options), TimeProvider.System);
            await Task.WhenAll(runner.ExpireStaleAsync(token), runner.ExpireStaleAsync(token));
            await using var verify = new ServiceObservabilityDbContext(options);
            var intervals = await verify.HealthStateIntervals.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(2, intervals);
            Assert.AreEqual(evidence.AddSeconds(65), intervals[0].EndedAt);
            Assert.AreEqual(intervals[0].EndedAt, intervals[1].StartedAt);
            Assert.AreEqual((byte)EvaluatedHealth.Unknown, intervals[1].State);
            Assert.AreEqual(0, await verify.HealthObservations.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task RunOnce_RejectsObservationAfterAuthorizationOrSettingsChange(int change) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? server = null;
            try
            {
                var options = Options(database.ConnectionString);
                var targetId = await SeedAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                server = ServeAsync(listener, requestReceived, release, cancellation.Token);
                using var runner = new HealthRunner(new PooledDbContextFactory<ServiceObservabilityDbContext>(options), TimeProvider.System);
                var scan = runner.RunOnceAsync(cancellation.Token);
                await requestReceived.Task.WaitAsync(cancellation.Token);
                await using (var db = new ServiceObservabilityDbContext(options))
                {
                    if (change == 1) (await db.HealthNetworkPolicies.SingleAsync(token)).ReplaceNetworks([], DateTimeOffset.UtcNow);
                    else if (change == 2)
                    {
                        var service = await db.Services.SingleAsync(token);
                        service.Revoke(DateTimeOffset.UtcNow);
                        await db.SaveChangesAsync(token);
                        service.Resume(DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        (await db.HealthTargets.SingleAsync(token)).SetEnabled(false, DateTimeOffset.UtcNow);
                        (await db.HealthSchedules.SingleAsync(token)).Reset(false, DateTimeOffset.UtcNow);
                    }
                    await db.SaveChangesAsync(token);
                }
                release.SetResult();
                await scan;
                await server;
                await using var verify = new ServiceObservabilityDbContext(options);
                Assert.AreEqual(0, await verify.HealthObservations.CountAsync(token));
                if (change == 0) Assert.IsFalse((await verify.HealthSchedules.SingleAsync(x => x.TargetId == targetId, token)).Enabled);
            }
            finally
            {
                cancellation.Cancel(); listener.Stop();
                if (server is not null) try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PolicyRefresh_CancelsAnInFlightProbeWithoutWaitingForScan() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? server = null;
            try
            {
                var options = Options(database.ConnectionString);
                await SeedAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                server = ServeAsync(listener, received, release, cancellation.Token);
                using var runner = new HealthRunner(new PooledDbContextFactory<ServiceObservabilityDbContext>(options), TimeProvider.System);
                var scan = runner.RunOnceAsync(cancellation.Token);
                await received.Task.WaitAsync(cancellation.Token);
                await using (var db = new ServiceObservabilityDbContext(options))
                {
                    (await db.HealthNetworkPolicies.SingleAsync(token)).ReplaceNetworks([], DateTimeOffset.UtcNow);
                    await db.SaveChangesAsync(token);
                }
                await runner.RefreshNetworkPolicyAsync(cancellation.Token);
                await scan.WaitAsync(TimeSpan.FromSeconds(3), cancellation.Token);
                await using var verify = new ServiceObservabilityDbContext(options);
                Assert.AreEqual(0, await verify.HealthObservations.CountAsync(token));
            }
            finally
            {
                cancellation.Cancel(); listener.Stop();
                if (server is not null) try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            }
        }, context.CancellationToken);

    private static DbContextOptions<ServiceObservabilityDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;

    private static async Task<Guid> SeedAsync(DbContextOptions<ServiceObservabilityDbContext> options, int port, CancellationToken token)
    {
        await using var db = new ServiceObservabilityDbContext(options);
        await db.Database.MigrateAsync(token);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var source = new ServiceSource("source", new byte[32], now);
        var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
        var target = new HealthTarget(service.Id, null, $"http://127.0.0.1:{port}/health", 30, 5, 3, 2, now);
        target.SetEnabled(true, now);
        db.Sources.Add(source); db.Services.Add(service); db.HealthTargets.Add(target);
        db.HealthSchedules.Add(new HealthSchedule(target.Id, true, now));
        db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 0, 0, now));
        db.HealthNetworkPolicies.Add(new HealthNetworkPolicy(["127.0.0.0/8"], now));
        await db.SaveChangesAsync(token);
        return target.Id;
    }

    private static async Task ServeAsync(TcpListener listener, TaskCompletionSource? received, TaskCompletionSource? release, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        received?.SetResult();
        if (release is not null) await release.Task.WaitAsync(token);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nHealthy"u8.ToArray(), token);
    }
}
