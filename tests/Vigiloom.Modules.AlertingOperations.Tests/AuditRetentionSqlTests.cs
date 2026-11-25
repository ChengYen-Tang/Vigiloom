using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AlertingOperations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AuditRetentionSqlTests(TestContext context)
{
    [AssemblyCleanup] public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PreviewUpdateAndCleanupPreserveRecentAudit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var setup = new OperationsDbContext(options)) await setup.Database.MigrateAsync(token);
            await using (var setup = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options)) await setup.Database.MigrateAsync(token);
            var factory = new PooledDbContextFactory<OperationsDbContext>(options);
            var protection = new EphemeralDataProtectionProvider();
            IAuditRetentionSource[] sources = [];
            var settings = new AuditRetentionSettings(factory, new OperationAuditWriter(), protection, sources);
            var initial = await settings.GetAsync(token);
            Assert.AreEqual(365, initial.Days);
            var now = DateTimeOffset.UtcNow;
            var actor = Guid.CreateVersion7();
            var resource = Guid.CreateVersion7();
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details) VALUES ({actor}, {resource}, {now.AddDays(-40)}, {1}, {"{}"}), ({actor}, {resource}, {now.AddDays(-10)}, {1}, {"{}"})", token);
            }
            var preview = await settings.PreviewAsync(new(30, initial.Version), actor, token);
            Assert.IsNotNull(preview);
            Assert.AreEqual(1L, preview.AffectedRecords);
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(30, initial.Version, null), actor, token));
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new(30, initial.Version, preview.ConfirmationToken), Guid.CreateVersion7(), token));
            var saved = await settings.UpdateAsync(new(30, initial.Version, preview.ConfirmationToken), actor, token);
            Assert.IsNotNull(saved);
            Assert.AreEqual(30, saved.Days);
            Assert.IsNull(await settings.UpdateAsync(new(30, initial.Version, preview.ConfirmationToken), actor, token));
            var audit = new OperationAuditQueryService(factory);
            var before = await audit.QueryAuditRetentionAsync(new(now.AddDays(-1), now.AddDays(1)), token);
            Assert.AreEqual(30, before.Items.Single(x => x.AuditRetention is not null).AuditRetention!.CurrentDays);
            Assert.IsEmpty((await audit.QueryAsync(new(now.AddDays(-1), now.AddDays(1)), token)).Items);
            var next = await settings.PreviewAsync(new(20, saved.Version), actor, token);
            Assert.IsNotNull(next);
            var failing = new AuditRetentionSettings(factory, new FailingAuditWriter(), protection, sources);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.UpdateAsync(new(20, saved.Version, next.ConfirmationToken), actor, token));
            Assert.AreEqual(saved, await settings.GetAsync(token));
            Assert.AreEqual(1L, await new AuditRetentionCleanup(factory, settings, sources).RunAsync(token));
            await using var verify = await factory.CreateDbContextAsync(token);
            var count = await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM operations.AuditEvents").SingleAsync(token);
            Assert.AreEqual(2, count);
        }, context.CancellationToken);

    [TestMethod]
    public Task AlertCleanupPreservesOpenEpisodesAndPendingDelivery() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var setup = new OperationsDbContext(options)) await setup.Database.MigrateAsync(token);
            await using (var setup = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options)) await setup.Database.MigrateAsync(token);
            var factory = new PooledDbContextFactory<OperationsDbContext>(options);
            IAuditRetentionSource[] sources = [];
            var settings = new AuditRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider(), sources);
            var original = await settings.GetAsync(token);
            var now = DateTimeOffset.UtcNow; var old = now.AddDays(-40); var recent = now.AddDays(-10);
            var openPing = Guid.CreateVersion7(); var closedPing = Guid.CreateVersion7();
            var openService = Guid.CreateVersion7(); var closedService = Guid.CreateVersion7();
            var expiredNotice = Guid.CreateVersion7(); var queuedNotice = Guid.CreateVersion7();
            var pendingNotice = Guid.CreateVersion7(); var recentNotice = Guid.CreateVersion7();
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.PingAlerts (Id, DeviceId, Target, OpenedAt, RecoveredAt) VALUES ({openPing}, {Guid.CreateVersion7()}, {"a"}, {old}, {null}), ({closedPing}, {Guid.CreateVersion7()}, {"b"}, {old}, {old})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.PingAlertEvents (MessageId, AlertId, Generation, OccurredAt, IsRecovery, DeviceName, GroupsJson) VALUES ({Guid.CreateVersion7()}, {openPing}, {1}, {old}, {false}, {"a"}, {"[]"}), ({Guid.CreateVersion7()}, {closedPing}, {1}, {old}, {true}, {"b"}, {"[]"})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.ServiceHealthAlerts (Id, TargetId, ServiceId, State, OpenedAt, RecoveredAt) VALUES ({openService}, {Guid.CreateVersion7()}, {Guid.CreateVersion7()}, {1}, {old}, {null}), ({closedService}, {Guid.CreateVersion7()}, {Guid.CreateVersion7()}, {0}, {old}, {old})", token);
                foreach (var (id, at) in new[] { (expiredNotice, old), (queuedNotice, old), (pendingNotice, old), (recentNotice, recent) })
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.Notifications (Id, MessageId, Source, OccurredAt, Title, Detail) VALUES ({id}, {Guid.CreateVersion7()}, {"ping"}, {at}, {"title"}, {"detail"})", token);
                var cursor = await db.Database.SqlQuery<long>($"SELECT RowId AS [Value] FROM operations.Notifications WHERE Id = {queuedNotice}").SingleAsync(token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.LineSettings (Id, Slot, Enabled, LastNotificationRowId) VALUES ({Guid.CreateVersion7()}, {1}, {true}, {cursor})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.LineDeliveries (Id, NotificationId, GroupId, Recipient, Text, CreatedAt) VALUES ({Guid.CreateVersion7()}, {queuedNotice}, {Guid.CreateVersion7()}, {"group"}, {"pending"}, {old})", token);
            }
            var actor = Guid.CreateVersion7();
            var preview = await settings.PreviewAsync(new(30, original.Version), actor, token);
            Assert.IsNotNull(preview);
            Assert.AreEqual(2L, preview.AlertEpisodes); Assert.AreEqual(2L, preview.Notifications);
            Assert.IsNotNull(await settings.UpdateAsync(new(30, original.Version, preview.ConfirmationToken), actor, token));
            Assert.AreEqual(4L, await new AuditRetentionCleanup(factory, settings, sources).RunAsync(token));
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.PingAlerts WHERE Id = {openPing}").SingleAsync(token));
                Assert.AreEqual(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.PingAlertEvents WHERE AlertId = {closedPing}").SingleAsync(token));
                Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.ServiceHealthAlerts WHERE Id = {openService}").SingleAsync(token));
                Assert.AreEqual(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.ServiceHealthAlerts WHERE Id = {closedService}").SingleAsync(token));
                Assert.AreEqual(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.Notifications WHERE Id = {expiredNotice}").SingleAsync(token));
                Assert.AreEqual(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM operations.Notifications").SingleAsync(token));
                Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.LineDeliveries WHERE NotificationId = {queuedNotice} AND CompletedAt IS NULL").SingleAsync(token));
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE operations.LineDeliveries SET CompletedAt = {now} WHERE NotificationId = {queuedNotice}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE operations.LineSettings SET LastNotificationRowId = (SELECT MAX(RowId) FROM operations.Notifications)", token);
            }
            Assert.AreEqual(1L, await new AuditRetentionCleanup(factory, settings, sources).RunAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task BackgroundCleanupKeepsActiveWorkAndRemovesExpiredTerminalDeliveries() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var setup = new OperationsDbContext(options)) await setup.Database.MigrateAsync(token);
            await using (var setup = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options)) await setup.Database.MigrateAsync(token);
            var factory = new PooledDbContextFactory<OperationsDbContext>(options);
            IAuditRetentionSource[] sources = [];
            var settings = new AuditRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider(), sources);
            var policy = await settings.GetAsync(token);
            var now = DateTimeOffset.UtcNow; var old = now.AddDays(-40);
            var queued = Guid.CreateVersion7(); var leased = Guid.CreateVersion7(); var completed = Guid.CreateVersion7();
            var failed = Guid.CreateVersion7(); var recentFailed = Guid.CreateVersion7();
            var temporalCompleted = Guid.CreateVersion7(); var temporalPending = Guid.CreateVersion7();
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                foreach (var (id, state, at) in new[] { (queued, 0, old), (leased, 1, old), (completed, 2, old), (failed, 3, old), (recentFailed, 3, now) })
                {
                    var finished = state == 2 ? at : (DateTimeOffset?)null;
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts, LastAttemptAt, CompletedAt) VALUES ({id}, {"line.push.v1"}, {"{}"}, {old}, {old}, {state}, {1}, {at}, {finished})", token);
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO operations.LineDeliveries (Id, GroupId, Recipient, Text, CreatedAt, CompletedAt) VALUES ({id}, {Guid.CreateVersion7()}, {"group"}, {"message"}, {old}, {finished})", token);
                }
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts, LastAttemptAt, CompletedAt) VALUES ({temporalCompleted}, {"temporal.service-work"}, {"{}"}, {old}, {old}, {2}, {1}, {old}, {old}), ({temporalPending}, {"temporal.service-work"}, {"{}"}, {old}, {old}, {0}, {1}, {old}, {null})", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO scheduling.WorkReceipts (MessageId, PayloadHash, Result, CompletedAt) VALUES ({temporalCompleted}, {new byte[32]}, {"ok"}, {old})", token);
            }
            var status = new LineDeliveryStatus(factory);
            Assert.AreEqual("failed", (await status.GetAsync(failed, token))?.Status);
            var actor = Guid.CreateVersion7();
            var preview = await settings.PreviewAsync(new(30, policy.Version), actor, token);
            Assert.IsNotNull(preview); Assert.AreEqual(3L, preview.BackgroundJobs);
            Assert.IsNotNull(await settings.UpdateAsync(new(30, policy.Version, preview.ConfirmationToken), actor, token));
            Assert.AreEqual(3L, await new AuditRetentionCleanup(factory, settings, sources).RunAsync(token));
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                Assert.AreEqual(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.Outbox WHERE MessageId = {temporalCompleted}").SingleAsync(token));
                Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.Outbox WHERE MessageId = {temporalPending}").SingleAsync(token));
                Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.WorkReceipts WHERE MessageId = {temporalCompleted}").SingleAsync(token));
            }
            Assert.IsNull(await status.GetAsync(completed, token)); Assert.IsNull(await status.GetAsync(failed, token));
            Assert.AreEqual("pending", (await status.GetAsync(queued, token))?.Status);
            Assert.AreEqual("pending", (await status.GetAsync(leased, token))?.Status);
            Assert.AreEqual("failed", (await status.GetAsync(recentFailed, token))?.Status);
        }, context.CancellationToken);

    private sealed class FailingAuditWriter : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated audit write failure.");
    }

}
