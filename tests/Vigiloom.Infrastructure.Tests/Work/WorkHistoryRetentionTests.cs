using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Work;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class WorkHistoryRetentionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task RemovesOnlyExpiredTerminalTemporalDispatches() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = """
                    INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts, CompletedAt)
                    VALUES (@oldDone, @type, N'{}', @old, @old, 2, 1, @old),
                           (@oldFailed, @type, N'{}', @old, @old, 3, 1, NULL),
                           (@pending, @type, N'{}', @old, @old, 0, 0, NULL),
                           (@leased, @type, N'{}', @old, @old, 1, 1, NULL),
                           (@newDone, @type, N'{}', @now, @now, 2, 1, @now),
                           (@line, N'line.push.v1', N'{}', @old, @old, 2, 1, @old);
                    """;
                foreach (var name in new[] { "oldDone", "oldFailed", "pending", "leased", "newDone", "line" })
                    setup.Parameters.AddWithValue("@" + name, Guid.CreateVersion7());
                setup.Parameters.AddWithValue("@type", TemporalOutboxHandler.Type);
                setup.Parameters.AddWithValue("@old", DateTimeOffset.UtcNow.AddDays(-10));
                setup.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
                await setup.ExecuteNonQueryAsync(token);
            }

            var retention = new WorkHistoryRetention(database.ConnectionString);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
            Assert.AreEqual(2L, await retention.CountAsync(cutoff, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(cutoff, 1, token));
            Assert.AreEqual(1, await retention.DeleteBatchAsync(cutoff, 500, token));
            Assert.AreEqual(0L, await retention.CountAsync(cutoff, token));
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox";
            Assert.AreEqual(4, (int)(await count.ExecuteScalarAsync(token))!);
        }, TestContext.CancellationToken);
}
