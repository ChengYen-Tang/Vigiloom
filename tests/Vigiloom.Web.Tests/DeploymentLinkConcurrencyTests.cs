using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.ServiceDetails;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DeploymentLinkConcurrencyTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task EndingAndRecreating_AreSerializedWithoutOverlappingPeriods(bool snapshotReads) => WithReadModeAsync(snapshotReads, async (connectionString, token) =>
    {
        using var host = PingTestSite.CreateFactory(connectionString);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var services = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var links = host.Services.GetRequiredService<IDbContextFactory<DeploymentLinksDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var actor = Guid.CreateVersion7();
        var device = new Device("concurrent", "127.0.0.1", true, now);
        var source = new ServiceSource("concurrent", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "orders", "test", "Orders", now);
        var service = new MonitoredService(candidate, now);
        var instance = Guid.CreateVersion7();
        await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        await using (var db = await services.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service); await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instance},{service.Id},{source.Id},{"node"},{now},{now})", token);
        }
        var writer = new OperationAuditWriter();
        var normal = new DeploymentLinks(links, resources, services, writer);
        var input = new CreateDeploymentLink(device.Id, instance, "Verified deployment");
        var first = await normal.CreateAsync(service.Id, input, actor, token);
        Assert.AreEqual(DeploymentLinkStatus.Saved, first.Status);
        var active = (await normal.ListAsync(service.Id, 1, token))!.Items.Single();
        var pause = new PauseEndTime();
        var endingLinks = new PooledDbContextFactory<DeploymentLinksDbContext>(new DbContextOptionsBuilder<DeploymentLinksDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(DeploymentLinksDbContext.MigrationHistory, "resources"))
            .AddInterceptors(pause).Options);
        var ending = new DeploymentLinks(endingLinks, resources, services, writer);
        var waiting = new SourceLockObserver();
        var monitored = new PooledDbContextFactory<ServiceObservabilityDbContext>(new DbContextOptionsBuilder<ServiceObservabilityDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service"))
            .AddInterceptors(waiting).Options);
        var creating = new DeploymentLinks(links, resources, monitored, writer);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<DeploymentLinkStatus>? end = null;
        Task<DeploymentLinkResult>? create = null;
        Exception? failure = null;
        try
        {
            end = ending.EndAsync(service.Id, active.Id, new EndDeploymentLink(active.Version), actor, cancellation.Token);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            create = creating.CreateAsync(service.Id, input, actor, cancellation.Token);
            var sessionId = await waiting.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            await WaitForSqlBlockAsync(connectionString, sessionId, token);
            pause.Release.TrySetResult();
            Assert.AreEqual(DeploymentLinkStatus.Saved, await end.WaitAsync(TimeSpan.FromSeconds(20), token));
            Assert.AreEqual(DeploymentLinkStatus.Saved, (await create.WaitAsync(TimeSpan.FromSeconds(20), token)).Status);
            await using var db = await links.CreateDbContextAsync(token);
            var periods = await db.Links.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(2, periods);
            var endedAt = periods[0].EndedAt ?? throw new AssertFailedException("The first relationship must end.");
            Assert.IsGreaterThanOrEqualTo(endedAt, periods[1].StartedAt);
            Assert.IsNull(periods[1].EndedAt);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            pause.Release.TrySetResult();
            List<Exception> cleanupErrors = [];
            try { await cancellation.CancelAsync(); } catch (Exception ex) { cleanupErrors.Add(ex); }
            foreach (var task in new Task?[] { end, create })
            {
                if (task is null) continue;
                try { await task; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex) { if (!ReferenceEquals(ex, failure)) cleanupErrors.Add(ex); }
            }
            if (cleanupErrors.Count > 0)
            {
                if (failure is not null) cleanupErrors.Insert(0, failure);
                throw new AggregateException("Concurrent deployment operations failed.", cleanupErrors);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });

    private static async Task WaitForSqlBlockAsync(string connectionString, int sessionId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @session AND blocking_session_id > 0";
        command.Parameters.AddWithValue("@session", sessionId);
        while (Convert.ToInt32(await command.ExecuteScalarAsync(deadline.Token)) == 0) await Task.Delay(50, deadline.Token);
    }

    private Task WithReadModeAsync(bool snapshotReads, Func<string, CancellationToken, Task> test) => snapshotReads
        ? SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
            await using (var connection = new SqlConnection(master.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{database.DatabaseName}] SET READ_COMMITTED_SNAPSHOT ON";
                await command.ExecuteNonQueryAsync(token);
            }
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<DeploymentLinksDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var connection = new SqlConnection(database.ConnectionString)) { await connection.OpenAsync(token); await ReadCommittedSnapshot.EnsureAsync(connection, true, token); }
            await test(database.ConnectionString, token);
        }, context.CancellationToken)
        : ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            await using (var connection = new SqlConnection(database.ConnectionString)) { await connection.OpenAsync(token); await ReadCommittedSnapshot.EnsureAsync(connection, false, token); }
            await test(database.ConnectionString, token);
        }, context.CancellationToken);

    private sealed class PauseEndTime : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (command.CommandText.Contains("TODATETIMEOFFSET(SYSUTCDATETIME()", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return result;
        }
    }

    private sealed class SourceLockObserver : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[service].[Sources] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal) && command.Connection is SqlConnection connection)
            {
                await using var session = connection.CreateCommand();
                session.Transaction = (SqlTransaction?)command.Transaction;
                session.CommandText = "SELECT @@SPID";
                Entered.TrySetResult(Convert.ToInt32(await session.ExecuteScalarAsync(cancellationToken)));
            }
            return result;
        }
    }
}
