using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PublicationHealthSqlTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PublicationAndSourceRevocation_OverlapAndComplete(bool snapshotReads) => WithReadModeAsync(snapshotReads, async (connectionString, token) =>
    {
        using var host = PingTestSite.CreateFactory(connectionString);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var normal = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var publishing = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var source = new ServiceSource("concurrent source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "concurrent", "prod", "Concurrent", now);
        var service = new MonitoredService(candidate, now);
        var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 1, 1, now);
        target.SetEnabled(true, now);
        var policy = new HealthNetworkPolicy(["127.0.0.0/8"], now);
        var schedule = new HealthSchedule(target.Id, true, now);
        await using (var db = await normal.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            db.HealthTargets.Add(target); db.HealthSchedules.Add(schedule); db.HealthNetworkPolicies.Add(policy);
            db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 1, 1, now));
            await db.SaveChangesAsync(token);
            schedule.SetEvaluation(1, now, 1, 1, now, now);
            schedule.SetVersions(source.Version, service.Version, null, target.Version, policy.Version);
            await db.SaveChangesAsync(token);
        }
        var gate = new PausePublicationWithSourceLock();
        var attempted = new SourceLockAttempt();
        IDbContextFactory<ServiceObservabilityDbContext> WithInterceptor(DbCommandInterceptor interceptor) =>
            new PooledDbContextFactory<ServiceObservabilityDbContext>(new DbContextOptionsBuilder<ServiceObservabilityDbContext>()
                .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service"))
                .AddInterceptors(interceptor).Options);
        var admin = new PublicationAdministration(resources, WithInterceptor(gate), publishing, new OperationAuditWriter(), TimeProvider.System,
            NullLogger<PublicationAdministration>.Instance);
        var sourceAdmin = new SourceAdministration(WithInterceptor(attempted), new OperationAuditWriter());
        var settings = await admin.ReadAsync(PublicationResourceKind.Service, service.Id, token);
        Assert.IsNotNull(settings);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var publish = admin.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Public concurrent", PublicationPolicy.ServiceHistoryDetailsVersion, settings.Version), Guid.CreateVersion7(), cancellation.Token);
        Task<bool>? revoke = null;
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            revoke = sourceAdmin.RevokeAsync(source.Id, Guid.CreateVersion7(), cancellation.Token);
            await attempted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            gate.Release.TrySetResult();
            var result = await publish.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.AreEqual(PublicationChangeStatus.Updated, result.Status);
            Assert.IsTrue(await revoke.WaitAsync(TimeSpan.FromSeconds(20), token));
            await using var verify = await normal.CreateDbContextAsync(token);
            Assert.IsNotNull((await verify.Sources.SingleAsync(x => x.Id == source.Id, token)).RevokedAt);
            Assert.AreEqual((byte)0, (await verify.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).State);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            gate.Release.TrySetResult();
            List<Exception> errors = [];
            try { await cancellation.CancelAsync(); }
            catch (Exception ex) { errors.Add(ex); }
            foreach (var task in new Task?[] { publish, revoke })
            {
                if (task is null) continue;
                try { await task; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!ReferenceEquals(ex, failure)) errors.Add(ex); }
            }
            if (errors.Count > 0)
            {
                if (failure is not null) errors.Insert(0, failure);
                throw new AggregateException("Publication and source revocation failed.", errors);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Publication_DuringProbeRejectsOldResultAndAllowsFreshProbe(bool failConfirmation, bool snapshotReads) => WithReadModeAsync(snapshotReads, async (connectionString, token) =>
    {
        using var host = PingTestSite.CreateFactory(connectionString);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var services = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var publishing = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? firstServer = null;
        Task? secondServer = null;
        Task? thirdServer = null;
        Task? scan = null;
        Task<PublicationChangeResult>? publication = null;
        Exception? failure = null;
        var fence = new PauseAfterFenceTime(failConfirmation);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("probe source", new byte[32], now);
            var candidate = new ServiceCandidate(source.Id, "probe", "prod", "Probe", now);
            var service = new MonitoredService(candidate, now);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var target = new HealthTarget(service.Id, null, $"http://127.0.0.1:{port}/health", 30, 30, 1, 1, now);
            target.SetEnabled(true, now);
            await using (var db = await services.CreateDbContextAsync(token))
            {
                db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
                db.HealthTargets.Add(target); db.HealthNetworkPolicies.Add(new HealthNetworkPolicy(["127.0.0.0/8"], now));
                db.HealthSchedules.Add(new HealthSchedule(target.Id, true, now));
                db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 0, 0, now));
                await db.SaveChangesAsync(token);
            }
            using var runner = new HealthRunner(services, TimeProvider.System);
            firstServer = ServeProbeAsync(listener, received, release, cancellation.Token);
            scan = runner.RunOnceAsync(cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(connectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(fence).Options;
            var admin = new PublicationAdministration(resources, new PooledDbContextFactory<ServiceObservabilityDbContext>(options), publishing, new OperationAuditWriter(), TimeProvider.System,
                NullLogger<PublicationAdministration>.Instance);
            var before = await admin.ReadAsync(PublicationResourceKind.Service, service.Id, token);
            Assert.IsNotNull(before);
            publication = admin.SetAsync(PublicationResourceKind.Service, service.Id,
                new(true, "Public probe", PublicationPolicy.ServiceHistoryDetailsVersion, before.Version), Guid.CreateVersion7(), cancellation.Token);
            await fence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            release.TrySetResult();
            await Task.WhenAll(scan, firstServer).WaitAsync(TimeSpan.FromSeconds(15), token);
            await using (var db = await services.CreateDbContextAsync(token))
            {
                Assert.AreEqual(0, await db.HealthObservations.CountAsync(x => x.TargetId == target.Id, token));
                Assert.AreEqual((byte)0, (await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).State);
            }
            secondServer = ServeProbeAsync(listener, null, null, cancellation.Token);
            await runner.RunOnceAsync(token);
            await secondServer.WaitAsync(TimeSpan.FromSeconds(15), token);
            await using (var db = await services.CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await db.HealthObservations.CountAsync(x => x.TargetId == target.Id, token));
                Assert.AreEqual((byte)1, (await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).State);
            }
            fence.Release.TrySetResult();
            var published = await publication.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.AreEqual(PublicationChangeStatus.Updated, published.Status);
            Assert.IsNotNull(published.Settings);
            if (failConfirmation)
            {
                Assert.IsFalse(published.Settings.HistoryReady ?? true, "A failed post-commit confirmation must remain pending.");
                await using (var db = await services.CreateDbContextAsync(token))
                {
                    Assert.AreEqual((byte)1, (await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).State,
                        "A failed confirmation must roll its health reset back.");
                    Assert.IsNull((await db.HealthStateIntervals.SingleAsync(x => x.TargetId == target.Id && x.State == 1, token)).EndedAt);
                }
                var retried = await admin.SetAsync(PublicationResourceKind.Service, service.Id,
                    new(true, "Public probe", PublicationPolicy.ServiceHistoryDetailsVersion, published.Settings.Version), Guid.CreateVersion7(), token);
                Assert.AreEqual(PublicationChangeStatus.Updated, retried.Status);
                Assert.IsTrue(retried.Settings!.HistoryReady ?? false);
            }
            else Assert.IsTrue(published.Settings.HistoryReady ?? false);
            await using (var db = await services.CreateDbContextAsync(token))
            {
                var healthy = await db.HealthStateIntervals.SingleAsync(x => x.TargetId == target.Id && x.State == 1, token);
                Assert.IsNotNull(healthy.EndedAt);
                Assert.IsGreaterThan(healthy.StartedAt, healthy.EndedAt.Value, "Confirmation must not erase the health interval recorded while it waited.");
                Assert.AreEqual((byte)0, (await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).State);
            }
            thirdServer = ServeProbeAsync(listener, null, null, cancellation.Token);
            await runner.RunOnceAsync(token);
            await thirdServer.WaitAsync(TimeSpan.FromSeconds(15), token);
            await using (var db = await services.CreateDbContextAsync(token))
            {
                Assert.AreEqual(2, await db.HealthObservations.CountAsync(x => x.TargetId == target.Id, token));
                Assert.AreEqual((byte)1, (await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).State);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            List<Exception> cleanupFailures = [];
            release.TrySetResult();
            fence.Release.TrySetResult();
            try { await cancellation.CancelAsync(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            try { listener.Stop(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            foreach (var task in new Task?[] { scan, firstServer, secondServer, thirdServer, publication })
            {
                if (task is null) continue;
                try { await task; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
                catch (Exception ex) { if (!ReferenceEquals(ex, failure)) cleanupFailures.Add(ex); }
            }
            if (cleanupFailures.Count > 0)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                throw new AggregateException("Probe test or cleanup failed.", cleanupFailures);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });

    private Task WithReadModeAsync(bool snapshotReads, Func<string, CancellationToken, Task> test) => snapshotReads
        ? SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var master = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(master.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{database.DatabaseName}] SET READ_COMMITTED_SNAPSHOT ON";
                await command.ExecuteNonQueryAsync(token);
            }
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString))
            { await connection.OpenAsync(token); await ReadCommittedSnapshot.EnsureAsync(connection, true, token); }
            await test(database.ConnectionString, token);
        }, context.CancellationToken)
        : ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString))
            { await connection.OpenAsync(token); await ReadCommittedSnapshot.EnsureAsync(connection, false, token); }
            await test(database.ConnectionString, token);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Publication_ResetsEvidenceAtomicallyAndNoOpPreservesIt() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var services = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var publishing = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
        var observed = DateTimeOffset.UtcNow.AddSeconds(-1);
        var source = new ServiceSource("health source", new byte[32], observed);
        var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", observed);
        var service = new MonitoredService(candidate, observed);
        var target = new HealthTarget(service.Id, null, "http://127.0.0.1/health", 30, 5, 1, 1, observed);
        target.SetEnabled(true, observed);
        var policy = new HealthNetworkPolicy(["127.0.0.0/8"], observed);
        var schedule = new HealthSchedule(target.Id, true, observed);
        await using (var db = await services.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            db.HealthTargets.Add(target); db.HealthNetworkPolicies.Add(policy); db.HealthSchedules.Add(schedule);
            db.HealthStateIntervals.Add(new HealthStateInterval(target.Id, 1, 1, observed));
            await db.SaveChangesAsync(token);
            schedule.SetEvaluation(1, observed, 1, 1, observed, observed);
            schedule.SetVersions(source.Version, service.Version, null, target.Version, policy.Version);
            await db.SaveChangesAsync(token);
        }
        var log = NullLogger<PublicationAdministration>.Instance;
        var normal = new PublicationAdministration(resources, services, publishing, new OperationAuditWriter(), TimeProvider.System, log);
        var failing = new PublicationAdministration(resources, services, publishing, new FailingAudit(), TimeProvider.System, log);
        var actor = Guid.CreateVersion7();
        var before = await normal.ReadAsync(PublicationResourceKind.Service, service.Id, token);
        Assert.IsNotNull(before);
        var change = new PublicationChange(true, "Public orders", PublicationPolicy.ServiceHistoryDetailsVersion, before.Version);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SetAsync(PublicationResourceKind.Service, service.Id, change, actor, token));
        await using (var db = await services.CreateDbContextAsync(token))
        {
            var rolledBack = await db.Services.SingleAsync(x => x.Id == service.Id, token);
            var retained = await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token);
            Assert.IsFalse(rolledBack.IsPublic);
            Assert.AreEqual(before.Version, Convert.ToBase64String(rolledBack.Version));
            Assert.AreEqual((byte)1, retained.State);
            Assert.AreEqual(0L, retained.Generation);
            Assert.AreEqual(1, await db.HealthStateIntervals.CountAsync(x => x.TargetId == target.Id && x.EndedAt == null && x.State == 1, token));
        }

        var published = await normal.SetAsync(PublicationResourceKind.Service, service.Id, change, actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, published.Status);
        Assert.IsNotNull(published.Settings);
        Assert.IsTrue(published.Settings.HistoryReady ?? false);
        long generation;
        await using (var db = await services.CreateDbContextAsync(token))
        {
            var reset = await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token);
            Assert.AreEqual((byte)0, reset.State);
            Assert.IsNull(reset.ServiceVersion);
            Assert.IsNull(reset.LeaseOwner);
            Assert.AreEqual(2L, reset.Generation, "Publication and post-commit confirmation both invalidate the old version.");
            generation = reset.Generation;
            Assert.AreEqual(1, await db.HealthStateIntervals.CountAsync(x => x.TargetId == target.Id && x.EndedAt == null && x.State == 0, token));
            Assert.AreEqual(1, await db.HealthStateIntervals.CountAsync(x => x.TargetId == target.Id && x.EndedAt != null && x.State == 1, token));
        }
        var noOp = await normal.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Public orders", PublicationPolicy.ServiceHistoryDetailsVersion, published.Settings.Version), actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, noOp.Status);
        await using (var db = await services.CreateDbContextAsync(token))
            Assert.AreEqual(generation, (await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).Generation);

        var renamed = await normal.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Public checkout", PublicationPolicy.ServiceHistoryDetailsVersion, noOp.Settings!.Version), actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, renamed.Status);
        var reduced = await normal.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Public checkout", PublicationPolicy.ServiceHistoryVersion, renamed.Settings!.Version), actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, reduced.Status);
        var expanded = await normal.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Public checkout", PublicationPolicy.ServiceHistoryDetailsVersion, reduced.Settings!.Version), actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, expanded.Status);
        var revoked = await normal.SetAsync(PublicationResourceKind.Service, service.Id,
            new(false, null, null, expanded.Settings!.Version), actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, revoked.Status);
        await using (var db = await services.CreateDbContextAsync(token))
            Assert.AreEqual(generation + 6, (await db.HealthSchedules.SingleAsync(x => x.TargetId == target.Id, token)).Generation);
    }, context.CancellationToken);

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken token) =>
            throw new InvalidOperationException("Simulated audit failure.");
    }

    private sealed class PauseAfterFenceTime(bool shouldFail) : DbCommandInterceptor
    {
        private int pauseOnce = 1;
        private int failConfirmation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("TODATETIMEOFFSET(SYSUTCDATETIME()", StringComparison.Ordinal) &&
                !command.CommandText.Contains("health-invalidation", StringComparison.Ordinal) && Interlocked.Exchange(ref pauseOnce, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (shouldFail) Volatile.Write(ref failConfirmation, 1);
            }
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref failConfirmation) == 1 && command.CommandText.Contains("UPDATE [service].[Services]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[PublicSince]", StringComparison.Ordinal) && Interlocked.Exchange(ref failConfirmation, 0) == 1)
                throw new DbUpdateException("Simulated failure after health invalidation.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PausePublicationWithSourceLock : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("health-invalidation", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class SourceLockAttempt : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[service].[Sources] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal)) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private static async Task ServeProbeAsync(TcpListener listener, TaskCompletionSource? received, TaskCompletionSource? release, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        received?.TrySetResult();
        if (release is not null) await release.Task.WaitAsync(token);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nHealthy"u8.ToArray(), token);
    }
}
