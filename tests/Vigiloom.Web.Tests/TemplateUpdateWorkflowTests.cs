using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemplateUpdateWorkflowTests(TestContext context)
{
    [TestMethod]
    public Task Confirm_RequiresMatchingActorAndUnchangedDeviceSet() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            using var scope = host.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TemplateAdministration>();
            var workflow = scope.ServiceProvider.GetRequiredService<TemplateUpdateWorkflow>();
            var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var actor = Guid.CreateVersion7();
            var template = (await service.CreateAsync(new("Original", true, true), actor, token)).Template!;
            var now = DateTimeOffset.UtcNow;
            var device = new Device("Device", "example.com", false, now);
            device.SetTemplatePublication("Public device", template.Id, false, now);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            var proposed = new PublicationTemplateUpdate("Updated", false, true);
            var preview = await workflow.PreviewAsync(template.Id, proposed, actor, token);
            Assert.IsNotNull(preview.Preview);
            Assert.AreEqual(1, preview.Preview.Impact.Count);
            Assert.AreEqual(TemplateChangeStatus.Invalid, (await workflow.ConfirmAsync(template.Id, new(preview.Preview.Confirmation), Guid.CreateVersion7(), token)).Status);
            Assert.AreEqual(TemplateChangeStatus.Invalid, (await workflow.ConfirmAsync(Guid.CreateVersion7(), new(preview.Preview.Confirmation), actor, token)).Status);
            Assert.AreEqual(TemplateChangeStatus.Invalid, (await workflow.ConfirmAsync(template.Id, new("invalid-token"), actor, token)).Status);
            await using (var db = await resources.CreateDbContextAsync(token))
            {
                var existing = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                existing.Rename("Changed", now.AddSeconds(1));
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(TemplateChangeStatus.Conflict, (await workflow.ConfirmAsync(template.Id, new(preview.Preview.Confirmation), actor, token)).Status);
            var refreshed = await workflow.PreviewAsync(template.Id, proposed, actor, token);
            Assert.IsNotNull(refreshed.Preview);
            var sp = scope.ServiceProvider;
            var failing = new TemplateUpdateWorkflow(sp.GetRequiredService<IDbContextFactory<PublishingDbContext>>(), resources,
                sp.GetRequiredService<TemplateReader>(), new FailingAudit(), sp.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failing.ConfirmAsync(template.Id, new(refreshed.Preview.Confirmation), actor, token));
            await using (var db = await sp.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
                Assert.AreEqual("Original", (await db.Templates.SingleAsync(x => x.Id == template.Id, token)).Name);
            var updated = await workflow.ConfirmAsync(template.Id, new(refreshed.Preview.Confirmation), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Updated, updated.Status);
            Assert.IsNotNull(updated.Template);
            Assert.AreEqual("Updated", updated.Template.Name);
            Assert.IsFalse(updated.Template.ShowLatency);
            Assert.AreEqual(TemplateChangeStatus.Conflict, (await workflow.ConfirmAsync(template.Id, new(refreshed.Preview.Confirmation), actor, token)).Status);
        }, context.CancellationToken);

    [TestMethod]
    public Task Confirmation_ExpiresWhilePreparingTransaction() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var actor = Guid.CreateVersion7();
            var template = (await sp.GetRequiredService<TemplateAdministration>().CreateAsync(new("Original", true, true), actor, token)).Template!;
            var clock = new AdvancingClock(DateTimeOffset.UtcNow);
            var workflow = new TemplateUpdateWorkflow(sp.GetRequiredService<IDbContextFactory<PublishingDbContext>>(),
                sp.GetRequiredService<IDbContextFactory<ResourcesDbContext>>(), sp.GetRequiredService<TemplateReader>(),
                sp.GetRequiredService<IOperationAuditWriter<OperationAudit>>(), sp.GetRequiredService<IDataProtectionProvider>(), clock);
            var preview = await workflow.PreviewAsync(template.Id, new("Updated", false, true), actor, token);
            Assert.IsNotNull(preview.Preview);
            var result = await workflow.ConfirmAsync(template.Id, new(preview.Preview.Confirmation), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Invalid, result.Status);
            await using var db = await sp.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual("Original", (await db.Templates.SingleAsync(x => x.Id == template.Id, token)).Name);
        }, context.CancellationToken);

    private sealed class AdvancingClock(DateTimeOffset start) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref calls) <= 3 ? start : start.AddMinutes(11);
    }

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Test audit failure");
    }
}
