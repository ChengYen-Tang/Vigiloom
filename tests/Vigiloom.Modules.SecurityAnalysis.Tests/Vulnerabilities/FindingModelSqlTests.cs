using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class FindingModelSqlTests(TestContext context)
{
    [TestMethod]
    public Task Schema_BindsEvidenceToItsSourceAndProtectsHistory() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var now = DateTimeOffset.UtcNow;
            var source = new IntelligenceSource { Key = "test-a" };
            var other = new IntelligenceSource { Key = "test-b" };
            var revision = NewRevision(source.Id, 1);
            var wrongRevision = NewRevision(other.Id, 2);
            var finding = new VulnerabilityFinding { DeviceId = Guid.CreateVersion7(), SourceId = source.Id,
                PackageKey = "openssl:amd64", AdvisoryId = "UBUNTU-CVE-2025-0012",
                IdentityHash = FindingIdentity.Hash("openssl:amd64", "UBUNTU-CVE-2025-0012"),
                FirstEvaluatedAt = now, LastEvaluatedAt = now, LastRevisionId = revision.Id, LastPackageSnapshotId = Guid.CreateVersion7() };
            var evaluation = new VulnerabilityEvaluation { RunId = Guid.CreateVersion7(), FindingId = finding.Id, SourceId = source.Id, RevisionId = revision.Id,
                AgentId = Guid.CreateVersion7(), HostSnapshotId = Guid.CreateVersion7(), PackageSnapshotId = Guid.CreateVersion7(),
                EvaluatedAt = now, MatcherVersion = "v1", SourcePackage = "openssl", SourceVersion = "1.0-1", BinaryVersion = "1.0-1", Ecosystem = "Ubuntu:24.04:LTS", Architecture = "amd64" };
            db.IntelligenceSources.AddRange(source, other);
            db.IntelligenceRevisions.AddRange(revision, wrongRevision);
            db.VulnerabilityFindings.Add(finding);
            db.VulnerabilityEvaluations.Add(evaluation);
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            var history = new VulnerabilityHistory(new Factory(options));
            var page = await history.GetAsync(finding.DeviceId, finding.Id, now.AddMinutes(-1), now.AddMinutes(1), null, token);
            Assert.IsNotNull(page);
            Assert.HasCount(1, page.Items);
            Assert.AreEqual("Ubuntu:24.04:LTS", page.Items[0].Ecosystem);
            Assert.AreEqual(revision.Id, page.Items[0].RevisionId);
            Assert.IsNull(await history.GetAsync(Guid.CreateVersion7(), finding.Id, now.AddMinutes(-1), now.AddMinutes(1), null, token));
            await Assert.ThrowsAsync<ArgumentException>(() => history.GetAsync(finding.DeviceId, finding.Id, now.AddMinutes(-1), now.AddMinutes(1), "invalid", token));
            db.VulnerabilityEvaluations.Add(new VulnerabilityEvaluation { RunId = Guid.CreateVersion7(), FindingId = finding.Id, SourceId = other.Id, RevisionId = wrongRevision.Id,
                AgentId = evaluation.AgentId, HostSnapshotId = evaluation.HostSnapshotId, PackageSnapshotId = evaluation.PackageSnapshotId,
                EvaluatedAt = now, MatcherVersion = "v1", SourcePackage = "openssl", SourceVersion = "1.0-1", BinaryVersion = "1.0-1", Ecosystem = "Ubuntu:24.04:LTS", Architecture = "amd64" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.ChangeTracker.Clear();
            db.VulnerabilityEvaluations.Add(new VulnerabilityEvaluation { RunId = evaluation.RunId, FindingId = finding.Id, SourceId = source.Id, RevisionId = revision.Id,
                AgentId = evaluation.AgentId, HostSnapshotId = evaluation.HostSnapshotId, PackageSnapshotId = evaluation.PackageSnapshotId,
                EvaluatedAt = now, MatcherVersion = "v1", SourcePackage = "openssl", SourceVersion = "1.0-1", BinaryVersion = "1.0-1", Ecosystem = "Ubuntu:24.04:LTS", Architecture = "amd64" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.ChangeTracker.Clear();
            var migrations = db.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, index);
            await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], token));
            Assert.AreEqual(1, await db.VulnerabilityEvaluations.CountAsync(token));
        }, context.CancellationToken);

    private static IntelligenceRevision NewRevision(Guid sourceId, byte value) => new()
    {
        SourceId = sourceId, UpstreamVersion = value.ToString(), RetrievedAt = DateTimeOffset.UtcNow,
        Digest = Enumerable.Repeat(value, 32).ToArray(), Artifact = [value], SchemaVersion = "v1",
        MappingVersion = "v1", LicenseEvidence = "test", RecordCount = 1
    };

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
}
