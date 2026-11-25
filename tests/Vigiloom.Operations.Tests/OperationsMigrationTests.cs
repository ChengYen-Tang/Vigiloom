using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Agent = Vigiloom.Modules.AgentManagement.Domain.Agent;
using OldHostLog = Vigiloom.Modules.AgentManagement.Domain.HostLog;
using OldContext = Vigiloom.Modules.ServiceObservability.Domain.Telemetry.LogContext;
using OldRecord = Vigiloom.Modules.ServiceObservability.Domain.Telemetry.LogRecord;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class OperationsMigrationTests(TestContext context)
{
    [TestMethod]
    public Task CopiesExistingHostAndApplicationLogsWithTheirContext() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            var sourceServices = new ServiceCollection();
            sourceServices.AddModuleSqlServer<ResourcesDbContext>(source.ConnectionString, "resources");
            sourceServices.AddModuleSqlServer<AgentManagementDbContext>(source.ConnectionString, "agent");
            sourceServices.AddModuleSqlServer<ServiceObservabilityDbContext>(source.ConnectionString, "service");
            sourceServices.AddModuleSqlServer<OperationsDbContext>(source.ConnectionString, "operations");
            sourceServices.AddModuleSqlServer<LogAdmissionDbContext>(source.ConnectionString, "logquota");
            using var sourceProvider = sourceServices.BuildServiceProvider();
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>(), token);
            await MigrateAsync(sourceProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>(), token);
            var targetServices = new ServiceCollection();
            targetServices.AddModuleSqlServer<OperationsDbContext>(target.ConnectionString, "operations");
            targetServices.AddModuleSqlServer<LogAdmissionDbContext>(target.ConnectionString, "logquota");
            targetServices.AddModuleSqlServer<CentralLogsDbContext>(target.ConnectionString, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
            using var targetProvider = targetServices.BuildServiceProvider();
            await MigrateAsync(targetProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>(), token);
            await MigrateAsync(targetProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>(), token);
            await MigrateAsync(targetProvider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>(), token);

            var now = DateTimeOffset.UtcNow;
            var sourceQuota = new PolicyAdministration(sourceProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>());
            Assert.IsNotNull(await sourceQuota.UpdateAsync(new LogQuotaValues(7, 14, 120, 240), "", Guid.CreateVersion7(),
                (_, _, _, _) => Task.CompletedTask, token));
            var notification = new Notification { MessageId = Guid.CreateVersion7(), Source = "test", Title = "Alert", Detail = "details", OccurredAt = now };
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            { db.Set<Notification>().Add(notification); await db.SaveChangesAsync(token); }
            var device = new Device("Server", "server.example", true, now);
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            var agent = new Agent(device.Id, 0, new byte[32], now);
            var record = new AgentLogRecord(Guid.CreateVersion7(), now, "host event", null, null, null, null, null);
            var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, Guid.CreateVersion7(),
                AgentLogSourceKind.TextFile, "system", 1, [record]);
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            { db.Agents.Add(agent); db.HostLogs.Add(new OldHostLog(agent.Id, batch, 0, now)); await db.SaveChangesAsync(token); }
            var telemetrySource = new ServiceSource("source", new byte[32], now);
            var application = new MonitoredService(new ServiceCandidate(telemetrySource.Id, "orders", "production", "Orders", now), now);
            var logContext = new OldContext { ServiceId = application.Id, ResourcePayload = [1], ScopePayload = [2], ResourceSchemaUrl = "resource", ScopeSchemaUrl = "scope" };
            await using (var db = await sourceProvider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            {
                db.Sources.Add(telemetrySource); db.Services.Add(application);
                db.LogRecords.Add(new OldRecord { ServiceId = application.Id, Context = logContext, OccurredAt = now, ReceivedAt = now, RecordPayload = [3] });
                await db.SaveChangesAsync(token);
            }
            await OperationsDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token);
            await using var logs = await targetProvider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token);
            Assert.IsTrue(await logs.HostLogs.AnyAsync(x => x.AgentId == agent.Id && x.MessageId == record.MessageId && x.Content == "host event", token));
            var copied = await logs.LogRecords.Include(x => x.Context).SingleAsync(token);
            Assert.AreEqual(application.Id, copied.ServiceId);
            CollectionAssert.AreEqual(new byte[] { 3 }, copied.RecordPayload);
            CollectionAssert.AreEqual(new byte[] { 1 }, copied.Context.ResourcePayload);
            var targetQuota = new PolicyAdministration(targetProvider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>());
            Assert.AreEqual(120, (await targetQuota.GetAsync(token)).Values.GlobalRate);
            await using (var db = await targetProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
                Assert.IsTrue(await db.Set<Notification>().AnyAsync(x => x.Id == notification.Id && x.Title == "Alert", token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => OperationsDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token));
        }, outerToken), context.CancellationToken);

    private static async Task MigrateAsync<T>(IDbContextFactory<T> factory, CancellationToken token) where T : DbContext
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.MigrateAsync(token);
    }
}
