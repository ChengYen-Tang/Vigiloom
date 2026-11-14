using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemplateRetirementWorkflowTests(TestContext context)
{
    [TestMethod]
    public Task RetireDefault_RequiresReplacementAndPreservesDeviceAssignment() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var admin = sp.GetRequiredService<TemplateAdministration>();
            var workflow = sp.GetRequiredService<TemplateRetirementWorkflow>();
            var resources = sp.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var publishing = sp.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
            var actor = Guid.CreateVersion7();
            var first = (await admin.CreateAsync(new("First", true, true), actor, token)).Template!;
            var second = (await admin.CreateAsync(new("Second", false, true), actor, token)).Template!;
            var selected = (await admin.SetDefaultAsync(first.Id, new(first.Version), actor, token)).Template!;
            var device = new Device("Device", "example.com", false, DateTimeOffset.UtcNow);
            device.SetTemplatePublication("Public device", first.Id, false, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            Assert.AreEqual(TemplateChangeStatus.Invalid, (await workflow.PreviewAsync(first.Id, new(null, null), actor, token)).Status);
            var choice = new PublicationTemplateRetirement(second.Id, second.Version);
            var preview = await workflow.PreviewAsync(first.Id, choice, actor, token);
            Assert.IsNotNull(preview.Preview);
            Assert.AreEqual(1, preview.Preview.Impact.PublicCount);
            Assert.AreEqual(second.Id, preview.Preview.ReplacementDefault?.Id);
            Assert.AreEqual(TemplateChangeStatus.Invalid, await workflow.ConfirmAsync(first.Id, new(preview.Preview.Confirmation), Guid.CreateVersion7(), token));
            await using (var db = await resources.CreateDbContextAsync(token))
            {
                var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                row.Rename("Changed", DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(TemplateChangeStatus.Conflict, await workflow.ConfirmAsync(first.Id, new(preview.Preview.Confirmation), actor, token));
            var refreshed = await workflow.PreviewAsync(first.Id, choice, actor, token);
            Assert.IsNotNull(refreshed.Preview);
            var failing = new TemplateRetirementWorkflow(publishing, resources, sp.GetRequiredService<TemplateReader>(),
                new FailingAudit(), sp.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failing.ConfirmAsync(first.Id, new(refreshed.Preview.Confirmation), actor, token));
            await using (var db = await publishing.CreateDbContextAsync(token))
                Assert.AreEqual(first.Id, (await db.Templates.SingleAsync(x => x.IsDefault, token)).Id);
            var afterFailure = await sp.GetRequiredService<OperationAuditQueryService>().QueryPublicationsAsync(
                new(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.IsFalse(afterFailure.Items.Any(x => x.ResourceId == first.Id && x.PublicationTemplate?.Action == PublicationTemplateAction.Retired));
            Assert.AreEqual(TemplateChangeStatus.Updated, await workflow.ConfirmAsync(first.Id, new(refreshed.Preview.Confirmation), actor, token));
            await using (var db = await publishing.CreateDbContextAsync(token))
            {
                Assert.IsTrue((await db.Templates.SingleAsync(x => x.Id == first.Id, token)).IsRetired);
                Assert.AreEqual(second.Id, (await db.Templates.SingleAsync(x => x.IsDefault, token)).Id);
            }
            await using (var db = await resources.CreateDbContextAsync(token))
            {
                var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                Assert.AreEqual(first.Id, row.PublicTemplateId);
                Assert.IsTrue(row.IsPublic);
            }
            var audit = await sp.GetRequiredService<OperationAuditQueryService>().QueryPublicationsAsync(
                new(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.IsTrue(audit.Items.Any(x => x.ResourceId == first.Id && x.PublicationTemplate?.Action == PublicationTemplateAction.Retired));
            Assert.IsTrue(audit.Items.Any(x => x.ResourceId == second.Id && x.PublicationTemplate?.Action == PublicationTemplateAction.DefaultChanged));
            var lastPreview = await workflow.PreviewAsync(second.Id, new(null, null), actor, token);
            Assert.IsNotNull(lastPreview.Preview);
            var third = (await admin.CreateAsync(new("Third", true, true), actor, token)).Template!;
            Assert.AreEqual(TemplateChangeStatus.Conflict, await workflow.ConfirmAsync(second.Id, new(lastPreview.Preview.Confirmation), actor, token));
            var removeThird = await workflow.PreviewAsync(third.Id, new(null, null), actor, token);
            Assert.IsNotNull(removeThird.Preview);
            Assert.AreEqual(TemplateChangeStatus.Updated, await workflow.ConfirmAsync(third.Id, new(removeThird.Preview.Confirmation), actor, token));
            lastPreview = await workflow.PreviewAsync(second.Id, new(null, null), actor, token);
            Assert.IsNotNull(lastPreview.Preview);
            Assert.AreEqual(TemplateChangeStatus.Updated, await workflow.ConfirmAsync(second.Id, new(lastPreview.Preview.Confirmation), actor, token));
            audit = await sp.GetRequiredService<OperationAuditQueryService>().QueryPublicationsAsync(
                new(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.IsTrue(audit.Items.Any(x => x.ResourceId == second.Id && x.PublicationTemplate?.Action == PublicationTemplateAction.DefaultCleared));
            Assert.IsTrue(selected.IsDefault);
        }, context.CancellationToken);

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        private readonly OperationAuditWriter writer = new();
        private int count;
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            ++count == 2 ? throw new InvalidOperationException("Test audit failure") : writer.AppendAsync(connection, transaction, audit, cancellationToken);
    }
}
