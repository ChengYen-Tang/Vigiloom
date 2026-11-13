using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

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

            var update = management.UpdateAsync(device.Id,
                new DeviceInput("Renamed", "example.com", [], false, Version: Convert.ToBase64String(device.Version)), Guid.CreateVersion7(), token);
            await tx.CommitAsync(token);
            var result = await update;
            Assert.AreEqual(DeviceResultStatus.Success, result.Status);
            Assert.IsNotNull(result.Device);
            var archive = await management.SetArchivedAsync(device.Id, true, result.Device.Version, Guid.CreateVersion7(), token);
            Assert.AreEqual(DeviceResultStatus.Success, archive.Status);
        }, context.CancellationToken);

    private sealed class ContextFactory(DbContextOptions<ResourcesDbContext> options) : IDbContextFactory<ResourcesDbContext>
    {
        public ResourcesDbContext CreateDbContext() => new(options);
    }
}
