using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PublicationConcurrencyTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ServicePeriod_IsFencedAfterPublicationCommit() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var services = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var source = new ServiceSource("source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "orders", "production", "Orders", now);
        var service = new MonitoredService(candidate, now);
        await using (var db = await services.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            await db.SaveChangesAsync(token);
        }
        var gate = new PausingAuditWriter();
        var admin = new PublicationAdministration(host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>(), services,
            host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>(), gate, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicationAdministration>.Instance);
        var settings = await admin.ReadAsync(PublicationResourceKind.Service, service.Id, token);
        Assert.IsNotNull(settings);
        var publish = admin.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Orders", PublicationPolicy.BaselineVersion, settings.Version), Guid.CreateVersion7(), token);
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(token);
            await using var db = await services.CreateDbContextAsync(token);
            var preCommit = await db.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            gate.Release.TrySetResult();
            var result = await publish;
            Assert.AreEqual(PublicationChangeStatus.Updated, result.Status);
            var saved = await db.Services.AsNoTracking().SingleAsync(x => x.Id == service.Id, token);
            Assert.IsNotNull(saved.PublicSince);
            Assert.IsTrue(saved.PublicSince >= preCommit);
            Assert.AreNotEqual(MonitoredService.PendingPublicSince, saved.PublicSince);
            Assert.AreEqual(Convert.ToBase64String(saved.Version), result.Settings!.Version);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            gate.Release.TrySetResult();
            try { await publish; } catch when (failure is not null) { }
        }
    }, context.CancellationToken);

    private sealed class PausingAuditWriter : IOperationAuditWriter<OperationAudit>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            await new OperationAuditWriter().AppendAsync(connection, transaction, audit, token);
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ServicePeriod_FailedFenceCanBeRetriedWithoutRepublishing() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var normal = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var source = new ServiceSource("source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "orders", "production", "Orders", now);
        var service = new MonitoredService(candidate, now);
        await using (var db = await normal.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            await db.SaveChangesAsync(token);
        }
        var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(new FailFirstFence()).Options;
        var admin = new PublicationAdministration(host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>(),
            new PooledDbContextFactory<ServiceObservabilityDbContext>(options), host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>(),
            new OperationAuditWriter(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicationAdministration>.Instance);
        var settings = await admin.ReadAsync(PublicationResourceKind.Service, service.Id, token);
        Assert.IsNotNull(settings);
        var actor = Guid.CreateVersion7();
        var first = await admin.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Orders", PublicationPolicy.BaselineVersion, settings.Version), actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, first.Status);
        DateTimeOffset updatedAt;
        await using (var db = await normal.CreateDbContextAsync(token))
        {
            var pending = await db.Services.SingleAsync(x => x.Id == service.Id, token);
            Assert.AreEqual(MonitoredService.PendingPublicSince, pending.PublicSince);
            updatedAt = pending.UpdatedAt;
        }
        var retry = await admin.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Orders", PublicationPolicy.BaselineVersion, first.Settings!.Version), actor, token);
        Assert.AreEqual(PublicationChangeStatus.Updated, retry.Status);
        await using var verify = await normal.CreateDbContextAsync(token);
        var saved = await verify.Services.SingleAsync(x => x.Id == service.Id, token);
        Assert.IsNotNull(saved.PublicSince);
        Assert.AreNotEqual(MonitoredService.PendingPublicSince, saved.PublicSince);
        Assert.AreEqual(updatedAt, saved.UpdatedAt);
        Assert.AreEqual(Convert.ToBase64String(saved.Version), retry.Settings!.Version);
    }, context.CancellationToken);

    private sealed class FailFirstFence : DbCommandInterceptor
    {
        private int remaining = 1;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("TODATETIMEOFFSET(SYSUTCDATETIME()", StringComparison.Ordinal) && Interlocked.Exchange(ref remaining, 0) == 1)
                throw new DbUpdateException("Simulated post-commit fence failure.");
            return ValueTask.FromResult(result);
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ServicePeriod_OldFenceCannotOverwriteRepublishedPeriod() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var services = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var publishing = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var source = new ServiceSource("source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "orders", "production", "Orders", now);
        var service = new MonitoredService(candidate, now);
        await using (var db = await services.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            await db.SaveChangesAsync(token);
        }
        var gate = new PauseFence();
        var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).AddInterceptors(gate).Options;
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicationAdministration>.Instance;
        var slow = new PublicationAdministration(resources, new PooledDbContextFactory<ServiceObservabilityDbContext>(options), publishing,
            new OperationAuditWriter(), TimeProvider.System, logger);
        var fast = new PublicationAdministration(resources, services, publishing, new OperationAuditWriter(), TimeProvider.System, logger);
        var settings = await slow.ReadAsync(PublicationResourceKind.Service, service.Id, token);
        Assert.IsNotNull(settings);
        var actor = Guid.CreateVersion7();
        var first = slow.SetAsync(PublicationResourceKind.Service, service.Id,
            new(true, "Orders", PublicationPolicy.BaselineVersion, settings.Version), actor, token);
        Exception? failure = null;
        try
        {
            await gate.Entered.Task.WaitAsync(token);
            var current = await fast.ReadAsync(PublicationResourceKind.Service, service.Id, token);
            Assert.IsNotNull(current);
            var revoked = await fast.SetAsync(PublicationResourceKind.Service, service.Id, new(false, null, null, current.Version), actor, token);
            Assert.AreEqual(PublicationChangeStatus.Updated, revoked.Status);
            var republished = await fast.SetAsync(PublicationResourceKind.Service, service.Id,
                new(true, "Orders", PublicationPolicy.BaselineVersion, revoked.Settings!.Version), actor, token);
            Assert.AreEqual(PublicationChangeStatus.Updated, republished.Status);
            await using var db = await services.CreateDbContextAsync(token);
            var newer = await db.Services.AsNoTracking().SingleAsync(x => x.Id == service.Id, token);
            gate.Release.TrySetResult();
            await first;
            var after = await db.Services.AsNoTracking().SingleAsync(x => x.Id == service.Id, token);
            Assert.AreEqual(newer.PublicSince, after.PublicSince);
            Assert.AreEqual(Convert.ToBase64String(newer.Version), Convert.ToBase64String(after.Version));
            Assert.AreNotEqual(MonitoredService.PendingPublicSince, after.PublicSince);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            gate.Release.TrySetResult();
            try { await first; } catch when (failure is not null) { }
        }
    }, context.CancellationToken);

    private sealed class PauseFence : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("TODATETIMEOFFSET(SYSUTCDATETIME()", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PublicationAuditFailure_RollsBackChange() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var device = new Device("router", "10.1.2.3", false, DateTimeOffset.UtcNow);
        await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var admin = new PublicationAdministration(resources, host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(),
            host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>(),
            new FailingAuditWriter(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicationAdministration>.Instance);
        var settings = await admin.ReadAsync(PublicationResourceKind.Device, device.Id, token);
        Assert.IsNotNull(settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.SetAsync(PublicationResourceKind.Device, device.Id,
            new(true, "Public router", PublicationPolicy.BaselineVersion, settings.Version), Guid.CreateVersion7(), token));
        await using var verify = await resources.CreateDbContextAsync(token);
        var persisted = await verify.Devices.SingleAsync(x => x.Id == device.Id, token);
        Assert.IsFalse(persisted.IsPublic);
        Assert.IsNull(persisted.PublicDisplayName);
    }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PublishAndArchive_WithSameVersion_CannotBothSucceed() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var device = new Device("router", "10.1.2.3", false, DateTimeOffset.UtcNow);
        await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        using var scope = host.Services.CreateScope();
        var publication = scope.ServiceProvider.GetRequiredService<PublicationAdministration>();
        var management = scope.ServiceProvider.GetRequiredService<DeviceManagement>();
        var settings = await publication.ReadAsync(PublicationResourceKind.Device, device.Id, token);
        Assert.IsNotNull(settings);
        var actor = Guid.CreateVersion7();
        var publish = publication.SetAsync(PublicationResourceKind.Device, device.Id,
            new(true, "Public router", PublicationPolicy.BaselineVersion, settings.Version), actor, token);
        var archive = management.SetArchivedAsync(device.Id, true, settings.Version, actor, token);
        await Task.WhenAll(publish, archive);
        Assert.AreNotEqual(publish.Result.Status == PublicationChangeStatus.Updated, archive.Result.Status == DeviceResultStatus.Success);
        await using var verify = await resources.CreateDbContextAsync(token);
        var persisted = await verify.Devices.SingleAsync(x => x.Id == device.Id, token);
        Assert.IsFalse(persisted.IsArchived && persisted.IsPublic);
    }, context.CancellationToken);

    private sealed class FailingAuditWriter : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated audit failure");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task GroupRead_RechecksMembersAfterConcurrentRevocation(bool detail, bool snapshotReads) => WithReadModeAsync(snapshotReads, async (connectionString, token) =>
    {
        using var host = PingTestSite.CreateFactory(connectionString);
        var now = DateTimeOffset.UtcNow;
        var device = new Device("router", "10.1.2.3", false, now);
        var group = new Group("group", now);
        device.SetPublication(true, "Public router", PublicationPolicy.BaselineVersion, now);
        group.SetPublication(true, "Public group", PublicationPolicy.BaselineVersion, now);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            db.Devices.Add(device); db.Groups.Add(group); db.DeviceGroups.Add(new DeviceGroup(group.Id, device.Id));
            await db.SaveChangesAsync(token);
        }
        var gate = new PauseSecondGroupQuery();
        var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(connectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).AddInterceptors(gate).Options;
        using var scope = host.Services.CreateScope();
        var reader = new PublicReader(new PooledDbContextFactory<ResourcesDbContext>(options),
            host.Services.GetRequiredService<IDbContextFactory<Vigiloom.Modules.ServiceObservability.Persistence.ServiceObservabilityDbContext>>(),
            scope.ServiceProvider.GetRequiredService<PingStore>(), scope.ServiceProvider.GetRequiredService<HealthStatusReader>());
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var read = detail ? ReadDetail() : ReadList();
        Exception? failure = null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                current.SetPublication(false, null, null, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            gate.Continue.TrySetResult();
            Assert.IsFalse(await read, "A group containing only a revoked resource must not be returned.");
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            gate.Continue.TrySetResult();
            if (!read.IsCompleted) await readCancellation.CancelAsync();
            try { await read; }
            catch when (failure is not null) { /* Preserve the original failure. */ }
        }

        async Task<bool> ReadDetail() => await reader.GroupAsync(group.Id, readCancellation.Token) is not null;
        async Task<bool> ReadList() => (await reader.GroupsAsync(null, 1, readCancellation.Token)).Items.Any();
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
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
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

    private sealed class PauseSecondGroupQuery : DbCommandInterceptor
    {
        private int count;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [resources].[Groups] AS g", StringComparison.Ordinal) && Interlocked.Increment(ref count) == 2)
            {
                Reached.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task DeviceRead_RechecksTemplateAfterConcurrentRestriction(bool detail, bool snapshotReads) => WithReadModeAsync(snapshotReads, async (connectionString, token) =>
    {
        using var host = PingTestSite.CreateFactory(connectionString);
        var now = DateTimeOffset.UtcNow;
        var template = new PublicationTemplate("Policy", true, true, now);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
        { db.Templates.Add(template); await db.SaveChangesAsync(token); }
        var device = new Device("router", "example.com", false, now);
        device.SetTemplatePublication("Public router", template.Id, false, now);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var gate = new PauseSecondDeviceQuery();
        var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(connectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).AddInterceptors(gate).Options;
        using var scope = host.Services.CreateScope();
        var reader = new PublicReader(new PooledDbContextFactory<ResourcesDbContext>(options),
            host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(),
            scope.ServiceProvider.GetRequiredService<PingStore>(), scope.ServiceProvider.GetRequiredService<HealthStatusReader>());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var read = detail ? ReadDetail() : ReadList();
        Exception? failure = null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            { var row = await db.Templates.SingleAsync(x => x.Id == template.Id, token); row.Update("Policy", false, false, DateTimeOffset.UtcNow); await db.SaveChangesAsync(token); }
            gate.Continue.TrySetResult();
            Assert.IsFalse(await read, "A query using an outdated publication policy must not return the device.");
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            gate.Continue.TrySetResult();
            if (!read.IsCompleted) await cancellation.CancelAsync();
            try { await read; } catch when (failure is not null) { }
        }
        async Task<bool> ReadDetail() => await reader.DeviceAsync(device.Id, cancellation.Token) is not null;
        async Task<bool> ReadList() => (await reader.DevicesAsync(null, 1, null, cancellation.Token)).Items.Any();
    });

    private sealed class PauseSecondDeviceQuery : DbCommandInterceptor
    {
        private int count;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [resources].[Devices] AS d", StringComparison.Ordinal) && Interlocked.Increment(ref count) == 2)
            { Reached.TrySetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
}
