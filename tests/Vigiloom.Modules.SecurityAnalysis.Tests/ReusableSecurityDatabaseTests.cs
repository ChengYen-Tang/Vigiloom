using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableSecurityDatabaseTests(TestContext context)
{
    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        await ReusableSecurityDatabase.DisposeAsync();
        SqlTestDatabase.CompleteRun();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cases_AreResetAndReuseOwnedDatabaseInEitherOrder(bool reverse)
    {
        string? firstDatabase = null;
        var sources = reverse ? new[] { InventorySource.LinuxRpm, InventorySource.LinuxDpkg } : new[] { InventorySource.LinuxDpkg, InventorySource.LinuxRpm };
        foreach (var source in sources)
            await ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
            {
                firstDatabase ??= database.DatabaseName;
                Assert.AreEqual(firstDatabase, database.DatabaseName);
                await using var db = Open(database.ConnectionString);
                Assert.AreEqual(0, await db.InventorySources.CountAsync(token));
                db.InventorySources.Add(new InventorySourceState(new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 0), source));
                await db.SaveChangesAsync(token);
            }, context.CancellationToken);
        await ReusableSecurityDatabase.ExecuteAsync(AssertCleanAsync, context.CancellationToken);
    }

    [TestMethod]
    public async Task FailedAndCancelledCases_AreResetBeforeReuse()
    {
        string? firstDatabase = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            firstDatabase = database.DatabaseName;
            await WriteAsync(database.ConnectionString, token);
            throw new InvalidOperationException("Expected failure");
        }, context.CancellationToken));
        await ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            Assert.AreEqual(firstDatabase, database.DatabaseName);
            await AssertCleanAsync(database, token);
        }, context.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            await WriteAsync(database.ConnectionString, token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }, cancellation.Token));
        await ReusableSecurityDatabase.ExecuteAsync(AssertCleanAsync, context.CancellationToken);
    }

    private static async Task WriteAsync(string connectionString, CancellationToken token)
    {
        await using var db = Open(connectionString);
        db.InventorySources.Add(new InventorySourceState(new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 0), InventorySource.LinuxDpkg));
        var now = DateTimeOffset.UtcNow;
        db.IntegrityChanges.Add(new IntegrityChangeRecord
        {
            AgentId = Guid.CreateVersion7(), DeviceId = Guid.CreateVersion7(), ScopeId = Guid.CreateVersion7(),
            ScopeRevision = 1, Generation = 1, ObservedAt = now, RecordedAt = now,
            Kind = (int)IntegrityChangeType.Added,
            Key = IntegrityKey.Create(IntegrityItemKind.File, ["test"]), AfterJson = "{}", CoverageGap = true
        });
        await db.SaveChangesAsync(token);
    }

    private static async Task AssertCleanAsync(SqlTestDatabase database, CancellationToken token)
    {
        await using var db = Open(database.ConnectionString);
        Assert.AreEqual(0, await db.InventorySources.CountAsync(token));
        Assert.AreEqual(0, await db.IntegrityChanges.CountAsync(token));
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [security].[__EFMigrationsHistory]";
        Assert.IsGreaterThan(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    }

    private static SecurityAnalysisDbContext Open(string connection) => new(new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
        .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options);
}
