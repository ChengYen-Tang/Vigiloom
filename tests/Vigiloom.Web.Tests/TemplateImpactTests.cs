using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemplateImpactTests(TestContext context)
{
    [TestMethod]
    public Task Preview_CoversAllAssignmentsAndDetectsChangesOutsidePage() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            await using var db = new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString).Options);
            var templateId = Guid.CreateVersion7();
            var version = new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 };
            var now = DateTimeOffset.UtcNow;
            var devices = Enumerable.Range(0, 52).Select(i => new Device($"Device {i}", "127.0.0.1", false, now)).ToArray();
            foreach (var device in devices) device.SetTemplatePublication("Public device", templateId, false, now);
            devices[0].SetPublication(false, null, null, now);
            db.Devices.AddRange(devices);
            await db.SaveChangesAsync(token);

            var first = await TemplateImpact.ReadAsync(db, templateId, version, 1, token);
            var second = await TemplateImpact.ReadAsync(db, templateId, version, 2, token);
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreEqual(52, first.Count);
            Assert.AreEqual(51, first.PublicCount);
            Assert.HasCount(50, first.Items);
            Assert.HasCount(2, second.Items);
            Assert.AreEqual(first.Fingerprint, second.Fingerprint);

            devices[51].Rename("Changed on second page", now.AddSeconds(1));
            await db.SaveChangesAsync(token);
            var updated = await TemplateImpact.ReadAsync(db, templateId, version, 1, token);
            Assert.IsNotNull(updated);
            Assert.AreNotEqual(first.Fingerprint, updated.Fingerprint);
            var newVersion = await TemplateImpact.ReadAsync(db, templateId, new byte[] { 0, 0, 0, 0, 0, 0, 0, 2 }, 1, token);
            Assert.IsNotNull(newVersion);
            Assert.AreNotEqual(updated.Fingerprint, newVersion.Fingerprint);

            devices[51].Archive(now.AddSeconds(2));
            await db.SaveChangesAsync(token);
            var archived = await TemplateImpact.ReadAsync(db, templateId, version, 1, token);
            Assert.IsNotNull(archived);
            Assert.AreEqual(51, archived.Count);
            Assert.AreNotEqual(updated.Fingerprint, archived.Fingerprint);
            devices[51].Restore(now.AddSeconds(3));
            await db.SaveChangesAsync(token);
            var restored = await TemplateImpact.ReadAsync(db, templateId, version, 1, token);
            Assert.IsNotNull(restored);
            Assert.AreEqual(52, restored.Count);
            Assert.AreNotEqual(archived.Fingerprint, restored.Fingerprint);
            devices[51].SetTemplatePublication("Public device", Guid.CreateVersion7(), false, now.AddSeconds(4));
            await db.SaveChangesAsync(token);
            var reassigned = await TemplateImpact.ReadAsync(db, templateId, version, 1, token);
            Assert.IsNotNull(reassigned);
            Assert.AreEqual(51, reassigned.Count);
            Assert.AreNotEqual(restored.Fingerprint, reassigned.Fingerprint);
        }, context.CancellationToken);
}
