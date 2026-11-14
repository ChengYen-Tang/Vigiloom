using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemplateAdministrationTests(TestContext context)
{
    [TestMethod]
    public Task Create_EnforcesUniqueNameAndAuditsAtomically() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var service = new TemplateAdministration(resources, new OperationAuditWriter(), TimeProvider.System);
            var actor = Guid.CreateVersion7();
            var result = await service.CreateAsync(new(" Network ", true, false), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Created, result.Status);
            Assert.IsNotNull(result.Template);
            Assert.AreEqual("Network", result.Template.Name);
            Assert.IsFalse(result.Template.IsDefault);
            Assert.AreEqual(TemplateChangeStatus.Conflict, (await service.CreateAsync(new("network", false, false), actor, token)).Status);
            Assert.AreEqual(TemplateChangeStatus.Invalid, (await service.CreateAsync(new(null, false, false), actor, token)).Status);

            var failing = new TemplateAdministration(resources, new FailingAudit(), TimeProvider.System);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failing.CreateAsync(new("Rollback", true, true), actor, token));
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await db.Templates.CountAsync(token));
            using var scope = host.Services.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<OperationAuditQueryService>();
            var now = DateTimeOffset.UtcNow;
            var audit = await query.QueryPublicationsAsync(new(now.AddMinutes(-1), now.AddMinutes(1)), token);
            Assert.HasCount(1, audit.Items);
            Assert.AreEqual(PublicationTemplateAction.Created, audit.Items[0].PublicationTemplate?.Action);
            Assert.AreEqual(result.Template.Id, audit.Items[0].ResourceId);
        }, context.CancellationToken);

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Test audit failure");
    }

    [TestMethod]
    public Task DefaultSwitch_PreservesAssignmentsAndRollsBackOnAuditFailure() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var service = new TemplateAdministration(resources, new OperationAuditWriter(), TimeProvider.System);
            var actor = Guid.CreateVersion7();
            var first = (await service.CreateAsync(new("First", true, false), actor, token)).Template!;
            var second = (await service.CreateAsync(new("Second", false, true), actor, token)).Template!;
            var third = (await service.CreateAsync(new("Third", true, true), actor, token)).Template!;
            var initial = await service.SetDefaultAsync(first.Id, new(first.Version), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Updated, initial.Status);
            var now = DateTimeOffset.UtcNow;
            await using (var db = await resources.CreateDbContextAsync(token))
            {
                var device = new Device("Device", "example.com", false, now);
                device.SetTemplatePublication("Public device", first.Id, false, now);
                db.Devices.Add(device);
                await db.SaveChangesAsync(token);
            }
            var failing = new TemplateAdministration(resources, new FailingAudit(), TimeProvider.System);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failing.SetDefaultAsync(second.Id, new(second.Version, first.Id, initial.Template!.Version), actor, token));
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            {
                Assert.AreEqual(first.Id, (await db.Templates.SingleAsync(x => x.IsDefault, token)).Id);
            }
            var switched = await service.SetDefaultAsync(second.Id, new(second.Version, first.Id, initial.Template!.Version), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Updated, switched.Status);
            Assert.IsNotNull(switched.Template);
            Assert.IsTrue(switched.Template.IsDefault);
            Assert.AreEqual(TemplateChangeStatus.Conflict, (await service.SetDefaultAsync(second.Id, new(second.Version), actor, token)).Status);
            Assert.AreEqual(TemplateChangeStatus.Conflict,
                (await service.SetDefaultAsync(third.Id, new(third.Version, first.Id, initial.Template!.Version), actor, token)).Status);
            await using (var db = await resources.CreateDbContextAsync(token))
            {
                var device = await db.Devices.SingleAsync(token);
                Assert.IsTrue(device.IsPublic);
                Assert.AreEqual(first.Id, device.PublicTemplateId);
            }
            await using var verify = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(second.Id, (await verify.Templates.SingleAsync(x => x.IsDefault, token)).Id);
        }, context.CancellationToken);
}
