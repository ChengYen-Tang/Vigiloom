using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TelemetryRetentionTests(TestContext context)
{
    [TestMethod]
    public Task CleanupPreservesRecentRecordsAndSharedContexts() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var factory = new Factory(options);
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("retention", new byte[32], now.AddDays(-30));
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now.AddDays(-30)), now.AddDays(-30));
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source); db.Services.Add(service);
                var sharedLog = new LogContext { ServiceId = service.Id, ResourcePayload = [], ScopePayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                var oldLog = new LogContext { ServiceId = service.Id, ResourcePayload = [], ScopePayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                for (var i = 0; i < 501; i++) db.LogRecords.Add(new LogRecord { ServiceId = service.Id, Context = sharedLog,
                    OccurredAt = now.AddDays(-15), ReceivedAt = now, RecordPayload = [] });
                db.LogRecords.Add(new LogRecord { ServiceId = service.Id, Context = sharedLog, OccurredAt = now.AddDays(-1), ReceivedAt = now, RecordPayload = [] });
                db.LogRecords.Add(new LogRecord { ServiceId = service.Id, Context = oldLog, OccurredAt = now.AddDays(-15), ReceivedAt = now, RecordPayload = [] });
                var sharedTrace = new TraceContext { ServiceId = service.Id, ResourcePayload = [], ScopePayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                var oldTrace = new TraceContext { ServiceId = service.Id, ResourcePayload = [], ScopePayload = [], ResourceSchemaUrl = "", ScopeSchemaUrl = "" };
                TraceSpan Span(TraceContext ctx, DateTimeOffset at) => new() { ServiceId = service.Id, Context = ctx,
                    StartedAt = at, EndedAt = at, ReceivedAt = now, TraceId = new byte[16], SpanId = new byte[8], SpanPayload = [] };
                db.TraceSpans.AddRange(Span(sharedTrace, now.AddDays(-8)), Span(sharedTrace, now.AddDays(-1)), Span(oldTrace, now.AddDays(-8)));
                await db.SaveChangesAsync(token);
            }
            var cleanup = new TelemetryRetentionCleanup(factory);
            Assert.AreEqual((502L, 2L), await cleanup.RunAsync(token));
            Assert.AreEqual((0L, 0L), await cleanup.RunAsync(token));
            await using var check = factory.CreateDbContext();
            Assert.AreEqual(1, await check.LogRecords.CountAsync(token));
            Assert.AreEqual(1, await check.LogContexts.CountAsync(token));
            Assert.AreEqual(1, await check.TraceSpans.CountAsync(token));
            Assert.AreEqual(1, await check.TraceContexts.CountAsync(token));
            Assert.AreEqual(now.AddDays(-1), (await check.LogRecords.SingleAsync(token)).OccurredAt);
            var history = new TelemetryHistory(factory);
            Assert.HasCount(1, (await history.LogsAsync(service.Id, now.AddDays(-2), now, 1, null, token)).Items);
            Assert.HasCount(1, (await history.TracesAsync(service.Id, now.AddDays(-2), now, 1, null, token)).Items);
        }, context.CancellationToken);

    [TestMethod]
    public Task SettingsRequirePreviewAndCommitWithAudit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var factory = new Factory(options);
            var operations = new OperationsFactory(operationsOptions);
            await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = operations.CreateDbContext()) await db.Database.MigrateAsync(token);
            var settings = new TelemetryRetentionSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider());
            var actor = Guid.CreateVersion7();
            var original = await settings.GetAsync(token);
            Assert.AreEqual(14, original.LogDays); Assert.AreEqual(7, original.TraceDays);
            Assert.AreEqual(30, original.MetricRawDays); Assert.AreEqual(365, original.MetricRollupDays);
            Assert.AreEqual(365, original.HealthDays);
            var input = new Vigiloom.Contracts.Services.TelemetryRetentionInput(10, 5, original.Version, null, 20, 300, 180);
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(input, actor, token));
            var preview = await settings.PreviewAsync(new(10, 5, original.Version, 20, 300, 180), actor, token);
            Assert.IsNotNull(preview);
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(input with { ConfirmationToken = preview.ConfirmationToken }, Guid.CreateVersion7(), token));
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(input with { ConfirmationToken = preview.ConfirmationToken, MetricRawDays = 19 }, actor, token));
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(input with { ConfirmationToken = preview.ConfirmationToken, HealthDays = 179 }, actor, token));
            var saved = await settings.UpdateAsync(input with { ConfirmationToken = preview.ConfirmationToken }, actor, token);
            Assert.IsNotNull(saved);
            Assert.AreEqual(10, saved.LogDays); Assert.AreEqual(5, saved.TraceDays);
            Assert.AreEqual(20, saved.MetricRawDays); Assert.AreEqual(300, saved.MetricRollupDays);
            Assert.AreEqual(180, saved.HealthDays);
            var source = new ServiceSource("retention settings", new byte[32], DateTimeOffset.UtcNow);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "settings", "test", "Settings", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            await using (var db = factory.CreateDbContext()) { db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token); }
            var details = await new ServiceDetailsReader(factory).ReadAsync(service.Id, token);
            Assert.IsNotNull(details);
            Assert.AreEqual(10, details.LogRetentionDays); Assert.AreEqual(5, details.TraceRetentionDays);
            Assert.AreEqual(180, details.HealthRetentionDays);
            Assert.IsNull(await settings.UpdateAsync(input with { ConfirmationToken = preview.ConfirmationToken }, actor, token));
            var audit = new OperationAuditQueryService(operations);
            var events = await audit.QueryTelemetryRetentionAsync(new OperationAuditQuery(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), token);
            Assert.HasCount(1, events.Items);
            Assert.AreEqual(14, events.Items[0].TelemetryRetention?.PreviousLogDays);
            Assert.AreEqual(5, events.Items[0].TelemetryRetention?.CurrentTraceDays);
            Assert.AreEqual(20, events.Items[0].TelemetryRetention?.CurrentMetricRawDays);
            Assert.AreEqual(180, events.Items[0].TelemetryRetention?.CurrentHealthDays);
            Assert.IsEmpty((await audit.QueryAsync(new(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), token)).Items);
            await using (var db = operations.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TABLE [operations].[AuditEvents]", token);
            await Assert.ThrowsAsync<SqlException>(() => settings.UpdateAsync(new(20, 10, saved.Version, null), actor, token));
            Assert.AreEqual(saved, await settings.GetAsync(token));
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<ServiceObservabilityDbContext> options) : IDbContextFactory<ServiceObservabilityDbContext>
    {
        public ServiceObservabilityDbContext CreateDbContext() => new(options);
    }

    private sealed class OperationsFactory(DbContextOptions<OperationsDbContext> options) : IDbContextFactory<OperationsDbContext>
    {
        public OperationsDbContext CreateDbContext() => new(options);
    }
}
