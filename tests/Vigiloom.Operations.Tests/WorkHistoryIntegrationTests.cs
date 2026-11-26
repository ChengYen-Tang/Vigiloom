using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class WorkHistoryIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task SharedDatabaseIsExcludedBeforeRemoteCleanup() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using var db = new SchedulingDbContext(options);
            await db.Database.MigrateAsync(token);
            var id = Guid.CreateVersion7();
            var old = DateTimeOffset.UtcNow.AddDays(-40);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts, CompletedAt)
                VALUES ({id}, {TemporalOutboxHandler.Type}, {"{}"}, {old}, {old}, {2}, {1}, {old})
                """, token);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new WorkHistoryRetention(database.ConnectionString));
            await using var app = builder.Build();
            app.MapWorkHistoryEndpoints();
            await app.StartAsync(token);
            var remote = new RemoteWorkHistory(app.GetTestClient());
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            var first = await remote.CountAsync(cutoff, [], token);
            Assert.AreEqual(1L, first.Count);
            var excluded = new[] { first.Database };
            Assert.AreEqual(0L, (await remote.CountAsync(cutoff, excluded, token)).Count);
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM scheduling.Outbox WHERE MessageId = {id}", token);
                Assert.AreEqual(0L, (await remote.DeleteBatchAsync(cutoff, 500, excluded, token)).Count);
                await transaction.RollbackAsync(token);
            }
            Assert.AreEqual(1L, (await remote.DeleteBatchAsync(cutoff, 500, [], token)).Count);
        }, context.CancellationToken);
}
