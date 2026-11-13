using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DeviceTemplatePublicationTests(TestContext context)
{
    [TestMethod]
    public Task ApplyTemplate_PreservesContinuousPeriodAndRestartsAfterRetirement() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var publishing = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var admin = new PublicationAdministration(resources, host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(), publishing, new OperationAuditWriter(), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicationAdministration>.Instance);
            var actor = Guid.CreateVersion7();
            var first = new PublicationTemplate("First", true, false, clock.GetUtcNow());
            var second = new PublicationTemplate("Second", false, true, clock.GetUtcNow());
            await using (var db = await publishing.CreateDbContextAsync(token)) { db.Templates.AddRange(first, second); await db.SaveChangesAsync(token); }
            var device = new Device("Device", "example.com", false, clock.GetUtcNow());
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            var initial = await admin.ReadAsync(PublicationResourceKind.Device, device.Id, token);
            Assert.IsNotNull(initial);
            var a = await admin.SetAsync(PublicationResourceKind.Device, device.Id,
                new(true, "Public device", PublicationPolicy.TemplateVersion, initial.Version, first.Id, Convert.ToBase64String(first.Version)), actor, token);
            Assert.AreEqual(PublicationChangeStatus.Updated, a.Status);
            Assert.IsNotNull(a.Settings);
            var start = clock.GetUtcNow();
            clock.Now = start.AddMinutes(2);
            var b = await admin.SetAsync(PublicationResourceKind.Device, device.Id,
                new(true, "Public device", PublicationPolicy.TemplateVersion, a.Settings.Version, second.Id, Convert.ToBase64String(second.Version)), actor, token);
            Assert.AreEqual(PublicationChangeStatus.Updated, b.Status);
            Assert.IsNotNull(b.Settings);
            await using (var db = await resources.CreateDbContextAsync(token)) Assert.AreEqual(start, (await db.Devices.SingleAsync(token)).PublicSince);
            await using (var db = await publishing.CreateDbContextAsync(token))
            {
                var row = await db.Templates.SingleAsync(x => x.Id == second.Id, token);
                row.Retire(clock.GetUtcNow());
                await db.SaveChangesAsync(token);
            }
            clock.Now = start.AddMinutes(4);
            var restored = await admin.SetAsync(PublicationResourceKind.Device, device.Id,
                new(true, "Public device", PublicationPolicy.BaselineVersion, b.Settings.Version), actor, token);
            Assert.AreEqual(PublicationChangeStatus.Updated, restored.Status);
            Assert.IsNotNull(restored.Settings);
            await using (var db = await resources.CreateDbContextAsync(token)) Assert.AreEqual(clock.Now, (await db.Devices.SingleAsync(token)).PublicSince);
            var stale = await admin.SetAsync(PublicationResourceKind.Device, device.Id,
                new(true, "Public device", PublicationPolicy.TemplateVersion, restored.Settings.Version, second.Id, Convert.ToBase64String(second.Version)), actor, token);
            Assert.AreEqual(PublicationChangeStatus.Conflict, stale.Status);
            var hidden = await admin.SetAsync(PublicationResourceKind.Device, device.Id,
                new(false, null, null, restored.Settings.Version), actor, token);
            Assert.AreEqual(PublicationChangeStatus.Updated, hidden.Status);
            using var scope = host.Services.CreateScope();
            var audit = await scope.ServiceProvider.GetRequiredService<OperationAuditQueryService>()
                .QueryPublicationsAsync(new(start.AddMinutes(-1), start.AddMinutes(10)), token);
            Assert.IsTrue(audit.Items.Any(x => x.ResourceId == device.Id && x.Publication is { TemplateChanged: true, PreviousPublic: true, CurrentPublic: true }));
            await using var verify = await resources.CreateDbContextAsync(token);
            var saved = await verify.Devices.SingleAsync(token);
            Assert.IsFalse(saved.IsPublic);
            Assert.IsNull(saved.PublicSince);
        }, context.CancellationToken);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public Task Revocation_DoesNotRequireTemplateTable() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var publishing = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await publishing.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var admin = new PublicationAdministration(resources, host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(), publishing, new OperationAuditWriter(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicationAdministration>.Instance);
            var now = DateTimeOffset.UtcNow;
            var device = new Device("Device", "example.com", false, now);
            device.SetTemplatePublication("Public device", Guid.CreateVersion7(), false, now);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC sp_rename N'[publishing].[Templates]', N'Templates_hidden_test'";
            await command.ExecuteNonQueryAsync(token);
            var settings = await admin.ReadAsync(PublicationResourceKind.Device, device.Id, token);
            Assert.IsNotNull(settings);
            Assert.IsNull(settings.TemplateVersion);
            var result = await admin.SetAsync(PublicationResourceKind.Device, device.Id,
                new(false, null, null, settings.Version), Guid.CreateVersion7(), token);
            Assert.AreEqual(PublicationChangeStatus.Updated, result.Status);
            await using var verify = await resources.CreateDbContextAsync(token);
            var persisted = await verify.Devices.SingleAsync(x => x.Id == device.Id, token);
            Assert.IsFalse(persisted.IsPublic);
            Assert.IsNull(persisted.PublicSince);
        }, context.CancellationToken);
}
