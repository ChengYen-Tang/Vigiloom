using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class PublicationMutationLockTests(TestContext context)
{
    [TestMethod]
    public Task PublicationLock_IsExclusiveAndDeviceEditsStillWork() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString).Options;
            await using var initial = new ResourcesDbContext(options);
            var device = new Device("Device", "example.com", false, DateTimeOffset.UtcNow);
            initial.Devices.Add(device);
            await initial.SaveChangesAsync(token);

            var factory = new ContextFactory(options);
            var management = new DeviceManagement(factory, TimeProvider.System);
            await using var locked = new ResourcesDbContext(options);
            await using var tx = await locked.Database.BeginTransactionAsync(token);
            Assert.IsTrue(await PublicationMutationLock.TryAcquireAsync(locked, token));

            // APPLOCK_TEST observes the lock from a separate transaction without waiting.
            await using var observer = new ResourcesDbContext(options);
            await using var observerTx = await observer.Database.BeginTransactionAsync(token);
            var canEnter = await observer.Database.SqlQueryRaw<int>("SELECT APPLOCK_TEST('public', 'Vigiloom.PublicationMutation', 'Exclusive', 'Transaction') AS [Value]").SingleAsync(token);
            Assert.AreEqual(0, canEnter);
            await observerTx.RollbackAsync(token);

            await tx.CommitAsync(token);
            var result = await management.UpdateAsync(device.Id,
                new DeviceInput("Renamed", "example.com", [], false, Version: Convert.ToBase64String(device.Version)), Guid.CreateVersion7(), token);
            Assert.AreEqual(DeviceResultStatus.Success, result.Status);
            Assert.IsNotNull(result.Device);
            var archive = await management.SetArchivedAsync(device.Id, true, result.Device.Version, Guid.CreateVersion7(), token);
            Assert.AreEqual(DeviceResultStatus.Success, archive.Status);
        }, context.CancellationToken);

    private sealed class ContextFactory(DbContextOptions<ResourcesDbContext> options) : IDbContextFactory<ResourcesDbContext>
    {
        public ResourcesDbContext CreateDbContext() => new(options);
    }

    [TestMethod]
    public Task DeviceUpdate_WaitsForPublicationLock() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            await using var initial = new ResourcesDbContext(options);
            await initial.Database.MigrateAsync(token);
            var device = new Device("Device", "example.com", false, DateTimeOffset.UtcNow);
            initial.Devices.Add(device);
            await initial.SaveChangesAsync(token);
            await using var holder = new ResourcesDbContext(options);
            var transaction = await holder.Database.BeginTransactionAsync(token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<DeviceResult>? update = null;
            Exception? failure = null;
            var cleanup = new List<Exception>();
            try
            {
                Assert.IsTrue(await PublicationMutationLock.TryAcquireAsync(holder, token));
                var holderSession = await holder.Database.SqlQueryRaw<int>("SELECT CAST(@@SPID AS int) AS [Value]").SingleAsync(token);
                await using var observer = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
                await observer.OpenAsync(token);
                await using var command = observer.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sys.dm_tran_locks h JOIN sys.dm_tran_locks w ON h.resource_type = w.resource_type AND h.resource_database_id = w.resource_database_id AND h.resource_description = w.resource_description WHERE h.request_session_id = @holder AND h.resource_database_id = DB_ID() AND h.resource_type = 'APPLICATION' AND h.request_status = 'GRANT' AND w.request_status = 'WAIT'";
                command.Parameters.AddWithValue("@holder", holderSession);
                var management = new DeviceManagement(new ContextFactory(options), TimeProvider.System);
                update = management.UpdateAsync(device.Id,
                    new DeviceInput("Renamed", "example.com", [], false, Version: Convert.ToBase64String(device.Version)), Guid.CreateVersion7(), cancellation.Token);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
                while (Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 0)
                {
                    if (update.IsCompleted || DateTimeOffset.UtcNow >= deadline) Assert.Fail("The device update did not wait for the publication lock.");
                    await Task.Delay(25, token);
                }
                await transaction.CommitAsync(token);
                Assert.AreEqual(DeviceResultStatus.Success, (await update).Status);
            }
            catch (Exception ex) { failure = ex; }
            try { await transaction.DisposeAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            if (update is not null)
            {
                if (!update.IsCompleted) { try { await cancellation.CancelAsync(); } catch (Exception ex) { cleanup.Add(ex); } }
                try { await update; } catch (Exception ex) when (failure is not null) { if (ex is not OperationCanceledException && !ReferenceEquals(ex, failure)) cleanup.Add(ex); }
            }
            if (failure is not null && cleanup.Count > 0) throw new AggregateException([failure, .. cleanup]);
            if (cleanup.Count > 0) throw new AggregateException(cleanup);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }, context.CancellationToken);
}
