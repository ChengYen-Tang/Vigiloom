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
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
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
    public Task PublicationAuditFailure_RollsBackChange() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var device = new Device("router", "10.1.2.3", false, DateTimeOffset.UtcNow);
        await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var admin = new PublicationAdministration(resources, host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(),
            new FailingAuditWriter(), TimeProvider.System);
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
            await test(database.ConnectionString, token);
        }, context.CancellationToken)
        : ReusableWebDatabase.ExecuteAsync((database, token) => test(database.ConnectionString, token), context.CancellationToken);

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
}
