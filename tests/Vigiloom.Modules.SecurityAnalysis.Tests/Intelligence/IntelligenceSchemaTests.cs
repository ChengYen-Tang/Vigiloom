using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IntelligenceSchemaTests(TestContext context)
{
    [TestMethod]
    public Task Migration_EmptyDowngradeWorks_AndRetainedSourcesPreventDowngrade() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_IntelligenceSources", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, index);
            var target = migrations[index - 1];
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(target, token);
            await migrator.MigrateAsync(null, token);
            db.IntelligenceSources.Add(new IntelligenceSource { Key = "retained" });
            await db.SaveChangesAsync(token);
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(target, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(1, await db.IntelligenceSources.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public async Task Activation_RequiresRevisionFromTheSameSource_AndFixtureCleansUp()
    {
        await ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            var first = new IntelligenceSource { Key = "first" };
            var second = new IntelligenceSource { Key = "second" };
            db.IntelligenceSources.AddRange(first, second);
            await db.SaveChangesAsync(token);
            var revision = new IntelligenceRevision
            {
                SourceId = first.Id, UpstreamVersion = "v1", ReleasedAt = DateTimeOffset.UtcNow,
                RetrievedAt = DateTimeOffset.UtcNow, Digest = new byte[32], Artifact = [1],
                SchemaVersion = "schema", MappingVersion = "raw-v1", LicenseEvidence = "license", RecordCount = 1
            };
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = second.Id, RevisionId = revision.Id, ActivatedAt = DateTimeOffset.UtcNow });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            Assert.IsInstanceOfType<SqlException>(error.InnerException);
            Assert.AreEqual(547, ((SqlException)error.InnerException).Number);
            db.ChangeTracker.Clear();
            db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = first.Id, RevisionId = revision.Id, ActivatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
        }, context.CancellationToken);
        await ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            Assert.AreEqual(0, await db.IntelligenceSources.CountAsync(token));
            Assert.AreEqual(0, await db.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await db.IntelligenceActivations.CountAsync(token));
        }, context.CancellationToken);
    }
}
