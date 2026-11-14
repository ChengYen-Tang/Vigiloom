using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryRecoveryTests(TestContext context)
{
    [TestMethod]
    public Task Recovery_AuditAndAbandonAreAtomic_AndRateLimitSurvives() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "retained", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
            AdvisoryImport import;
            await using (var db = contexts.CreateDbContext())
            {
                db.IntelligenceSources.Add(source);
                db.IntelligenceRevisions.Add(revision);
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(token);
                import = new AdvisoryImport { SourceId = source.Id, SourceVersion = source.Version, WindowStart = new(2023, 1, 1),
                    WindowEnd = new(2023, 2, 1), StartedAt = DateTimeOffset.UtcNow, FailureCode = "invalid-response", NotBefore = DateTimeOffset.UtcNow.AddHours(1) };
                db.AdvisoryImports.Add(import);
                await db.SaveChangesAsync(token);
            }
            var input = new AdvisoryImportRecoveryRequest(import.Id, Convert.ToBase64String(import.Version));
            var actor = Guid.CreateVersion7();
            var failing = new AdvisoryRecovery(contexts, new FailingAudit(), TimeProvider.System);
            Assert.AreEqual(AdvisoryRecoveryResult.Conflict, await failing.AbandonAsync(source.Id, input with { Version = Convert.ToBase64String(new byte[8]) }, actor, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.AbandonAsync(source.Id, input, actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                Assert.AreEqual(1, (await db.AdvisoryImports.SingleAsync(token)).Phase);
                Assert.AreEqual(revision.Id, (await db.IntelligenceActivations.SingleAsync(token)).RevisionId);
            }
            var recovery = new AdvisoryRecovery(contexts, new OperationAuditWriter(), TimeProvider.System);
            Assert.AreEqual(AdvisoryRecoveryResult.Abandoned, await recovery.AbandonAsync(source.Id, input, actor, token));
            Assert.AreEqual(AdvisoryRecoveryResult.Conflict, await recovery.AbandonAsync(source.Id, input, actor, token));
            var audit = await new OperationAuditQueryService(new OperationsFactory(operationsOptions))
                .QueryIntelligenceAsync(new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.HasCount(1, audit.Items);
            Assert.AreEqual(actor, audit.Items[0].ActorId);
            Assert.AreEqual(import.Id, audit.Items[0].AdvisoryImport?.ImportId);
            var next = await new AdvisoryImportStore(contexts, TimeProvider.System).BeginAsync(source.Id, import.WindowStart, import.WindowEnd, token);
            Assert.IsNotNull(next);
            Assert.IsNotNull(next.NotBefore);
            Assert.IsGreaterThan(DateTimeOffset.UtcNow, next.NotBefore.Value);
            Assert.AreEqual(AdvisoryRecoveryResult.Conflict, await recovery.AbandonAsync(source.Id,
                new AdvisoryImportRecoveryRequest(next.Id, Convert.ToBase64String(next.Version)), actor, token));
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
    private sealed class OperationsFactory(DbContextOptions<OperationsDbContext> options) : IDbContextFactory<OperationsDbContext>
    {
        public OperationsDbContext CreateDbContext() => new(options);
    }
    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Injected audit failure");
    }
}
