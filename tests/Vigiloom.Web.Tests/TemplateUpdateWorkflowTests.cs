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
    public Task HistoryConfirmation_RejectsStaleVersionsAndCanRetry() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            using var scope = host.Services.CreateScope();
            var resources = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var templates = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
            var administration = scope.ServiceProvider.GetRequiredService<TemplateAdministration>();
            var actor = Guid.CreateVersion7();
            var item = (await administration.CreateAsync(new("Original", true, true), actor, token)).Template!;
            byte[] oldVersion;
            await using (var db = await templates.CreateDbContextAsync(token))
            {
                var row = await db.Templates.SingleAsync(x => x.Id == item.Id, token);
                row.Update(row.Name, true, true, DateTimeOffset.UtcNow, 1);
                await db.SaveChangesAsync(token);
                oldVersion = row.Version;
            }
            // Interleave a real default switch between the settings commit and confirmation.
            var switched = await administration.SetDefaultAsync(item.Id, new(Convert.ToBase64String(oldVersion)), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Updated, switched.Status);
            Assert.IsNotNull(switched.Template);
            Assert.IsTrue(switched.Template.IsDefault);
            Assert.IsFalse(switched.Template.HistoryReady);
            var currentVersion = Convert.FromBase64String(switched.Template.Version);
            Assert.IsNull(await TemplateHistoryPeriod.ConfirmAsync(resources, item.Id, oldVersion, token));
            await using (var db = await templates.CreateDbContextAsync(token))
                Assert.AreEqual(Vigiloom.Modules.Publishing.Domain.PublicationTemplate.PendingHistorySince,
                    (await db.Templates.AsNoTracking().SingleAsync(x => x.Id == item.Id, token)).HistorySince);
            var workflow = scope.ServiceProvider.GetRequiredService<TemplateUpdateWorkflow>();
            var retry = await workflow.PreviewAsync(item.Id, new("Original", true, true), actor, token);
            Assert.IsNotNull(retry.Preview);
            var resumed = await workflow.ConfirmAsync(item.Id, new(retry.Preview.Confirmation), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Updated, resumed.Status);
            Assert.IsTrue(resumed.Template!.HistoryReady);
            Assert.IsNull(await TemplateHistoryPeriod.ConfirmAsync(resources, item.Id, currentVersion, token));
        }, context.CancellationToken);

    [TestMethod]
    public Task HistoryChanges_ArePreviewedAndBeginAfterCommit() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            using var scope = host.Services.CreateScope();
            var administration = scope.ServiceProvider.GetRequiredService<TemplateAdministration>();
            var workflow = scope.ServiceProvider.GetRequiredService<TemplateUpdateWorkflow>();
            var templates = host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>();
            var actor = Guid.CreateVersion7();
            var original = (await administration.CreateAsync(new("Original", true, true), actor, token)).Template!;
            var preview = await workflow.PreviewAsync(original.Id, new("Original", true, true, 3), actor, token);
            Assert.IsNotNull(preview.Preview);
            Assert.AreEqual(0, preview.Preview.Current.HistoryDays);
            Assert.AreEqual(3, preview.Preview.Proposed.HistoryDays);
            var before = DateTimeOffset.UtcNow.AddSeconds(-2);
            var changed = await workflow.ConfirmAsync(original.Id, new(preview.Preview.Confirmation), actor, token);
            Assert.AreEqual(TemplateChangeStatus.Updated, changed.Status);
            Assert.IsNotNull(changed.Template);
            Assert.AreEqual(3, changed.Template.HistoryDays);
            Assert.IsTrue(changed.Template.HistoryReady);
            DateTimeOffset firstStart;
            await using (var db = await templates.CreateDbContextAsync(token))
            {
                var row = await db.Templates.AsNoTracking().SingleAsync(x => x.Id == original.Id, token);
                Assert.IsNotNull(row.HistorySince);
                firstStart = row.HistorySince.Value;
                Assert.IsGreaterThanOrEqualTo(before, firstStart);
                Assert.IsLessThan(DateTimeOffset.UtcNow.AddSeconds(2), firstStart);
                Assert.AreEqual(changed.Template.Version, Convert.ToBase64String(row.Version));
            }
            Assert.AreEqual(TemplateChangeStatus.Conflict, (await workflow.ConfirmAsync(original.Id, new(preview.Preview.Confirmation), actor, token)).Status);
            var legacy = await workflow.PreviewAsync(original.Id, new("Renamed", false, true), actor, token);
            Assert.IsNotNull(legacy.Preview);
            Assert.AreEqual(3, legacy.Preview.Proposed.HistoryDays);
            var renamed = await workflow.ConfirmAsync(original.Id, new(legacy.Preview.Confirmation), actor, token);
            Assert.AreEqual(3, renamed.Template!.HistoryDays);
            await using (var db = await templates.CreateDbContextAsync(token))
                Assert.AreEqual(firstStart, (await db.Templates.AsNoTracking().SingleAsync(x => x.Id == original.Id, token)).HistorySince);
            var narrow = await workflow.PreviewAsync(original.Id, new("Renamed", false, true, 1), actor, token);
            Assert.IsNotNull(narrow.Preview);
            Assert.AreEqual(TemplateChangeStatus.Updated, (await workflow.ConfirmAsync(original.Id, new(narrow.Preview.Confirmation), actor, token)).Status);
            await using (var db = await templates.CreateDbContextAsync(token))
                Assert.AreEqual(firstStart, (await db.Templates.AsNoTracking().SingleAsync(x => x.Id == original.Id, token)).HistorySince);
            var expand = await workflow.PreviewAsync(original.Id, new("Renamed", false, true, 7), actor, token);
            Assert.IsNotNull(expand.Preview);
            var expanded = await workflow.ConfirmAsync(original.Id, new(expand.Preview.Confirmation), actor, token);
            Assert.IsTrue(expanded.Template!.HistoryReady);
            await using (var db = await templates.CreateDbContextAsync(token))
                Assert.IsGreaterThanOrEqualTo(firstStart, (await db.Templates.AsNoTracking().SingleAsync(x => x.Id == original.Id, token)).HistorySince!.Value);
            Assert.AreEqual(TemplateChangeStatus.Invalid, (await workflow.PreviewAsync(original.Id, new("Invalid", true, true, 8), actor, token)).Status);
        }, context.CancellationToken);

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
