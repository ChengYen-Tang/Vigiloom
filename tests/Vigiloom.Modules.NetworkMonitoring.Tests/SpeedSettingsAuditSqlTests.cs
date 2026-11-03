using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SpeedSettingsAuditSqlTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Update_CommitsTypedAuditAndRejectsStaleVersion() => WithDatabase(async (factory, connectionString, token) =>
    {
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var before = await settings.GetAsync(token);
        var actor = Guid.CreateVersion7();
        var input = new SpeedSettingsInput(false, 12, 100, 45, before.Version);
        var updated = await settings.UpdateAsync(input, actor, token);
        Assert.IsNotNull(updated);
        Assert.IsNull(await settings.UpdateAsync(input, actor, token), "A stale version must not create another audit event.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ActorId, ResourceId, Kind, Details, OccurredAt FROM operations.AuditEvents";
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        Assert.AreEqual(actor, reader.GetGuid(0));
        Assert.AreNotEqual(Guid.Empty, reader.GetGuid(1));
        Assert.AreEqual((short)1, reader.GetInt16(2));
        using var details = JsonDocument.Parse(reader.GetString(3));
        Assert.AreEqual(before.IntervalHours, details.RootElement.GetProperty("Previous").GetProperty("IntervalHours").GetInt32());
        Assert.AreEqual(12, details.RootElement.GetProperty("Current").GetProperty("IntervalHours").GetInt32());
        Assert.AreEqual(before.Version, details.RootElement.GetProperty("PreviousVersion").GetString());
        Assert.AreEqual(updated.Version, details.RootElement.GetProperty("CurrentVersion").GetString());
        Assert.AreEqual(4, details.RootElement.EnumerateObject().Count());
        Assert.AreEqual(TimeSpan.Zero, reader.GetFieldValue<DateTimeOffset>(4).Offset);
        Assert.IsFalse(await reader.ReadAsync(token));
    });

    [TestMethod]
    public Task Update_AuditInsertFailureRollsBackSettings() => WithDatabase(async (factory, _, token) =>
    {
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var before = await settings.GetAsync(token);
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow, State = SpeedTestState.Queued });
            await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE operations.AuditEvents", token);
        }
        await Assert.ThrowsAsync<SqlException>(() => settings.UpdateAsync(
            new SpeedSettingsInput(false, 12, 100, 45, before.Version), Guid.CreateVersion7(), token));
        var after = await settings.GetAsync(token);
        Assert.AreEqual(before.Automatic, after.Automatic);
        Assert.AreEqual(before.IntervalHours, after.IntervalHours);
        Assert.AreEqual(before.Version, after.Version);
        await using var verify = await factory.CreateDbContextAsync(token);
        Assert.AreEqual(SpeedTestState.Queued, (await verify.SpeedRuns.SingleAsync(token)).State);
    });

    [TestMethod]
    public Task ConcurrentUpdates_CommitExactlyOneChangeAndAuditEvent() => WithDatabase(async (factory, connectionString, token) =>
    {
        var first = new SpeedSettings(factory, new OperationAuditWriter());
        var second = new SpeedSettings(factory, new OperationAuditWriter());
        var before = await first.GetAsync(token);
        var input = new SpeedSettingsInput(false, 12, 100, 45, before.Version);
        var results = await Task.WhenAll(first.UpdateAsync(input, Guid.CreateVersion7(), token), second.UpdateAsync(input, Guid.CreateVersion7(), token));
        Assert.AreEqual(1, results.Count(x => x is not null));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operations.AuditEvents";
        Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    });

    [TestMethod]
    public Task Writer_RejectsMismatchedTransactionAndHonorsCancellation() => WithDatabase(async (_, connectionString, token) =>
    {
        await using var first = new SqlConnection(connectionString);
        await using var second = new SqlConnection(connectionString);
        await first.OpenAsync(token);
        await second.OpenAsync(token);
        await using var transaction = await first.BeginTransactionAsync(token);
        var values = new SpeedSettingsAuditValues(true, 6, 100, 45);
        var version = Convert.ToBase64String(new byte[8]);
        var audit = new OperationAudit(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow,
            new SpeedSettingsChanged(values, values, version, version));
        var writer = new OperationAuditWriter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(second, transaction, audit, token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => writer.AppendAsync(first, transaction, audit, cancelled.Token));
        await transaction.RollbackAsync(token);
        Assert.AreEqual(System.Data.ConnectionState.Open, first.State);
        await using var check = second.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM operations.AuditEvents";
        Assert.AreEqual(0, Convert.ToInt32(await check.ExecuteScalarAsync(token)));
    });

    [TestMethod]
    public Task Query_ReturnsBoundedPagesAndDoesNotExposeUnknownStoredDetails() => WithDatabase(async (factory, connectionString, token) =>
    {
        var settings = new SpeedSettings(factory, new OperationAuditWriter());
        var before = await settings.GetAsync(token);
        await settings.UpdateAsync(new SpeedSettingsInput(false, 12, 100, 45, before.Version), Guid.CreateVersion7(), token);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details)
                SELECT ActorId, ResourceId, OccurredAt, 1, N'{"Password":"secret-never-expose"}' FROM operations.AuditEvents;
                INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details)
                SELECT a.ActorId, a.ResourceId, a.OccurredAt, a.Kind, a.Details FROM operations.AuditEvents a
                CROSS JOIN (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14),(15),(16),(17),(18),(19),(20),(21),(22),(23),(24),(25)) n(i);
                INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details)
                SELECT TOP (1) ActorId, ResourceId, OccurredAt, Kind, JSON_MODIFY(Details, '$.Current.Automatic', NULL)
                FROM operations.AuditEvents WHERE Details NOT LIKE N'%Password%' ORDER BY RowId;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
        var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(connectionString).Options;
        var query = new OperationAuditQueryService(new OperationsFactory(options));
        var now = DateTimeOffset.UtcNow;
        var first = await query.QueryAsync(new(now.AddDays(-1), now.AddDays(1)), token);
        var second = await query.QueryAsync(new(now.AddDays(-1), now.AddDays(1), 2), token);
        Assert.HasCount(50, first.Items);
        Assert.IsTrue(first.HasMore);
        Assert.HasCount(3, second.Items);
        Assert.IsFalse(second.HasMore);
        Assert.DoesNotContain("secret-never-expose", JsonSerializer.Serialize(first));
        Assert.DoesNotContain("secret-never-expose", JsonSerializer.Serialize(second));
        Assert.AreEqual(27, first.Items.Concat(second.Items).Count(x => x.SpeedSettings is null));
    });

    private Task WithDatabase(Func<IDbContextFactory<NetworkDbContext>, string, CancellationToken, Task> action) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var network = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
            await using (var db = new NetworkDbContext(network)) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            await action(new Factory(network), database.ConnectionString, token);
        }, TestContext.CancellationToken);

    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext() => new(options);
    }

    private sealed class OperationsFactory(DbContextOptions<OperationsDbContext> options) : IDbContextFactory<OperationsDbContext>
    {
        public OperationsDbContext CreateDbContext() => new(options);
    }
}
