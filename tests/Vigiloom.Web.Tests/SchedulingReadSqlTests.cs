using System.Diagnostics;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class SchedulingReadSqlTests(TestContext testContext)
{
    [TestMethod]
    public Task AuditFeed_ReturnsOnlyApprovedActionsReferencesAndOutcomes() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var services = new ServiceCollection();
        services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
        await using var provider = services.BuildServiceProvider();
        var contexts = provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        var writer = new DashboardAuditStore(contexts);
        var actor = Guid.CreateVersion7();
        var ids = await writer.BeginAsync(actor, "/recurring/trigger", ["vigiloom-speed-test", "secret-unapproved-reference"], token);
        await writer.CompleteAsync(ids, "accepted", token);
        await writer.BeginAsync(actor, "secret-unapproved-action", ["1"], token);
        var now = DateTimeOffset.UtcNow;
        var page = await new SchedulingAuditFeed(contexts).QueryAsync(now.AddDays(-1), now.AddDays(1), 1, token);
        Assert.HasCount(3, page.Items);
        var recurring = page.Items.Where(item => item.Action == SchedulingAuditAction.RecurringTrigger).ToArray();
        Assert.HasCount(2, recurring);
        Assert.IsTrue(recurring.All(item => item.Outcome == SchedulingAuditOutcome.Accepted));
        Assert.AreEqual(1, recurring.Count(item => item.Reference == "vigiloom-speed-test"));
        Assert.AreEqual(1, recurring.Count(item => item.Reference is null));
        Assert.AreEqual(1, page.Items.Count(item => item.Action == SchedulingAuditAction.Unknown && item.Reference is null));
    }, testContext.CancellationToken);

    [TestMethod]
    public Task LockedHangfireRead_TimesOutAndRecovers() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        // A previously disposed Web test host can leave Hangfire's process-wide log provider behind.
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF";
            await command.ExecuteNonQueryAsync(token);
        }
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vigiloom"] = database.ConnectionString
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
        services.AddVigiloomScheduling(config);
        await using var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        using var scope = provider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<OutboxScheduleManager>();
        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync(token);
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(token);
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM [hangfire].[Hash] WITH (TABLOCKX, HOLDLOCK)";
            await command.ExecuteScalarAsync(token);
        }
        var watch = Stopwatch.StartNew();
        try
        {
            var failure = await Assert.ThrowsAsync<SqlException>(() => manager.GetSnapshotAsync(token));
            Assert.AreEqual(-2, failure.Number);
            Assert.IsLessThan(TimeSpan.FromSeconds(20), watch.Elapsed);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync(cleanup.Token);
        }
        var recovered = await manager.GetSnapshotAsync(token);
        Assert.AreEqual(1, recovered.Minutes);
        Assert.IsFalse(recovered.Applied);
    }, testContext.CancellationToken);
}
