using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IntegrityExpiryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task BusyFirstPage_DoesNotStarveLaterExpiredAndRetiredScopes() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var device = Guid.CreateVersion7();
            var actor = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 51; index++)
            {
                var agent = Guid.CreateVersion7();
                var scope = new IntegrityScopeSetting { DeviceId = device, Revision = 1, UpdatedBy = actor, UpdatedAt = now,
                    RetiredAt = now, DefinitionJson = "{}" };
                db.IntegrityScopes.Add(scope);
                db.IntegrityScopeStates.Add(new IntegrityScopeState { AgentId = agent, DeviceId = device, DeviceGeneration = 1,
                    ScopeId = scope.Id, ClosedGeneration = 1, CurrentGeneration = 1, ActiveGeneration = 2 });
                db.IntegrityScans.Add(Scan(agent, scope.Id, 1, IntegrityScanStatus.Complete));
                db.IntegrityScans.Add(Scan(agent, scope.Id, 2, IntegrityScanStatus.Staging));
                db.IntegrityChunks.Add(new IntegrityChunkData { AgentId = agent, ScopeId = scope.Id, Generation = 1, ChunkIndex = 0, Payload = [1, 2] });
            }
            await db.SaveChangesAsync(token);
            var orderedStates = await db.IntegrityScopeStates.AsNoTracking().OrderBy(x => x.RowId).Select(x => x.AgentId).ToArrayAsync(token);
            var orderedStaging = await db.IntegrityScans.AsNoTracking().Where(x => x.Generation == 2).OrderBy(x => x.RowId).Select(x => x.AgentId).ToArrayAsync(token);
            CollectionAssert.AreEqual(orderedStates, orderedStaging);
            var held = new List<(SqlConnection Connection, SqlTransaction Transaction)>();
            try
            {
                foreach (var agent in orderedStates.Take(50))
                {
                    var connection = new SqlConnection(database.ConnectionString);
                    await connection.OpenAsync(token);
                    var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                    held.Add((connection, transaction));
                    Assert.IsTrue(await AgentSnapshotLock.TryAcquireAsync(connection, transaction, "Integrity", agent, 0, token));
                }
                var expiry = new IntegrityExpiry(new Factory(options));
                Assert.AreEqual(2, await expiry.RunAsync(token));
                db.ChangeTracker.Clear();
                var last = await db.IntegrityScopeStates.AsNoTracking().SingleAsync(x => x.AgentId == orderedStates[50], token);
                Assert.IsNull(last.ActiveGeneration);
                Assert.IsNull(last.CurrentGeneration);
                Assert.AreEqual(2, last.ClosedGeneration);
                Assert.AreEqual(IntegrityCompletion.Expired, last.LastStatus);
                Assert.IsFalse(await db.IntegrityChunks.AnyAsync(x => x.AgentId == orderedStates[50], token));
                Assert.AreEqual(50, await db.IntegrityChunks.CountAsync(token));
            }
            finally
            {
                foreach (var (connection, transaction) in held)
                {
                    await transaction.DisposeAsync();
                    await connection.DisposeAsync();
                }
            }

            IntegrityScan Scan(Guid agent, Guid scope, long generation, IntegrityScanStatus status) => new()
            {
                AgentId = agent, DeviceId = device, DeviceGeneration = 1, ScopeId = scope, ScopeRevision = 1,
                SnapshotId = Guid.CreateVersion7(), Generation = generation, EnqueuedAt = now.AddDays(-2), ObservedAt = now.AddDays(-2),
                Outcome = IntegrityScanOutcome.Complete, ChunkCount = 1, TotalItems = 0, TotalBytes = 2, Digest = new byte[32],
                ReceivedChunks = status == IntegrityScanStatus.Complete ? 1 : 0, ReceivedItems = 0,
                ReceivedBytes = status == IntegrityScanStatus.Complete ? 2 : 0, StartedAt = now.AddDays(-2),
                FinishedAt = status == IntegrityScanStatus.Complete ? now.AddDays(-2) : null, Status = status
            };
        }, TestContext.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
}
