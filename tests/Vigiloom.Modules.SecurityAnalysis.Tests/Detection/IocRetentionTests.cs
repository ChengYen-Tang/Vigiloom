using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IocRetentionTests(TestContext context)
{
    [TestMethod]
    public Task Retention_RemovesExpiredEvidenceAndKeepsRecentEvidence() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = new string('a', 40), RetrievedAt = now,
                Digest = new byte[32], Artifact = [1], SchemaVersion = "1", MappingVersion = "1", LicenseEvidence = "test", Normalized = true };
            var agent = Guid.CreateVersion7(); var device = Guid.CreateVersion7();
            IocDetection Detection(DateTimeOffset at) => new() { AgentId = agent, DeviceId = device, MessageId = Guid.CreateVersion7(),
                SourceId = source.Id, RevisionId = revision.Id, Kind = "ip", Value = "192.0.2.1", Field = "DestinationIp", SourceLine = 1,
                ReceivedAt = at, EvaluatedAt = at };
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision);
                db.IocDetections.AddRange(Detection(now.AddDays(-15)), Detection(now.AddHours(-6)));
                await db.SaveChangesAsync(token);
            }
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                var retention = new SecurityDetectionRetention();
                Assert.AreEqual(1L, await retention.CountAsync(connection, transaction, now, 14, token));
                Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, transaction, now, 14, 500, token));
                await transaction.CommitAsync(token);
            }
            await using var verify = new SecurityAnalysisDbContext(options);
            var kept = await verify.IocDetections.SingleAsync(token);
            Assert.AreEqual(now.AddHours(-6), kept.ReceivedAt);
            var ruleTest = new SigmaRuleTest { Id = Guid.CreateVersion7(), SourceId = source.Id, RevisionId = revision.Id,
                Path = "rule.yml", Digest = "test", ActivationVersion = "test", MatcherVersion = "test", SamplesJson = "[]",
                ActorId = Guid.CreateVersion7(), TestedAt = now };
            var rule = new SigmaRuleActivation { Id = Guid.CreateVersion7(), SourceId = source.Id, TestId = ruleTest.Id,
                Path = "rule.yml", Enabled = true, UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now };
            verify.SigmaRuleTests.Add(ruleTest);
            verify.SigmaRuleActivations.Add(rule);
            await verify.SaveChangesAsync(token);
            SigmaDetectionResult Sigma(DateTimeOffset at) => new() { AgentId = agent, DeviceId = device, MessageId = Guid.CreateVersion7(),
                TestId = ruleTest.Id, ActivationId = rule.Id, ActivationVersion = rule.Version, Result = 1, ReceivedAt = at, EvaluatedAt = at };
            verify.SigmaDetectionResults.AddRange(Sigma(now.AddDays(-2)), Sigma(now.AddDays(-9)));
            SigmaCorrelationEvent Reference(DateTimeOffset at) => new() { TestId = ruleTest.Id, ActivationVersion = rule.Version,
                AgentId = agent, DeviceId = device, MessageId = Guid.CreateVersion7(), DeviceGeneration = 0, ReceivedAt = at, OccurredAt = at };
            verify.SigmaCorrelationEvents.AddRange(Reference(now.AddDays(-2)), Reference(now.AddDays(-9)));
            await verify.SaveChangesAsync(token);
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                var retention = new SecurityDetectionRetention();
                Assert.AreEqual(2L, await retention.CountAsync(connection, transaction, now, 1, token));
                Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, transaction, now, 1, 1, token));
                Assert.AreEqual(1, await retention.DeleteBatchAsync(connection, transaction, now, 1, 1, token));
                await transaction.CommitAsync(token);
            }
            verify.ChangeTracker.Clear();
            Assert.AreEqual(now.AddDays(-2), (await verify.SigmaDetectionResults.SingleAsync(token)).ReceivedAt);
            Assert.AreEqual(now.AddDays(-2), (await verify.SigmaCorrelationEvents.SingleAsync(token)).ReceivedAt);
        }, context.CancellationToken);
}
