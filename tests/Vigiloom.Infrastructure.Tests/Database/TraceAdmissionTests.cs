using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TraceAdmissionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task TracePolicy_IsIndependentVersionedAndRollsBackWithAuditFailure() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var factory = new PooledDbContextFactory<LogAdmissionDbContext>(options);
            var trace = new TracePolicyAdministration(factory);
            var log = new PolicyAdministration(factory);
            var original = await trace.GetAsync(token);
            Assert.AreEqual(LogQuotaValues.TraceDefault, original.Values);
            var updated = new LogQuotaValues(300, 1200, 2500, 4500);
            var actor = Guid.CreateVersion7();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => trace.UpdateAsync(updated, original.Version, actor,
                (_, _, _, _) => throw new InvalidOperationException("audit failed"), token));
            Assert.AreEqual(original, await trace.GetAsync(token));
            var saved = await trace.UpdateAsync(updated, original.Version, actor, (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(saved);
            Assert.AreEqual(updated, saved.Values);
            Assert.HasCount(8, Convert.FromBase64String(saved.Version));
            Assert.IsNull(await trace.UpdateAsync(updated, original.Version, actor, (_, _, _, _) => Task.CompletedTask, token));
            Assert.AreEqual(LogQuotaValues.Default, (await log.GetAsync(token)).Values);
            var small = await trace.UpdateAsync(new LogQuotaValues(1, 2, 1, 2), saved.Version, actor, (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(small);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            var source = Guid.CreateVersion7(); var service = Guid.CreateVersion7();
            await using (var tx = await connection.BeginTransactionAsync(token))
                Assert.AreEqual(LogAdmissionStatus.BatchTooLarge, (await TraceAdmission.AdmitAsync(connection, tx, [new(source, service, 3)], token)).Status);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await TraceAdmission.AdmitAsync(connection, tx, [new(source, service, 2)], token)).Status);
                await tx.CommitAsync(token);
            }
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task TraceQuota_SharesCentralCapacityAcrossServicesAndKeepsLogQuotaSeparate(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
                var id = Guid.CreateVersion7();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [logquota].[TracePolicies] ([Id],[Name],[SourceRate],[SourceBurst],[GlobalRate],[GlobalBurst],[CreatedAt],[UpdatedAt]) VALUES ({id},{"central"},{1},{100},{1},{100},TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
            }
            var source = Guid.CreateVersion7();
            var a = Guid.CreateVersion7();
            var b = Guid.CreateVersion7();
            async Task<LogAdmissionResult> Admit(Guid service)
            {
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var tx = await connection.BeginTransactionAsync(token);
                var result = await TraceAdmission.AdmitAsync(connection, tx, [new(source, service, 100)], token);
                await tx.CommitAsync(token);
                return result;
            }
            var outcomes = await Task.WhenAll(Admit(a), Admit(b));
            Assert.AreEqual(1, outcomes.Count(x => x.Status == LogAdmissionStatus.Accepted));
            Assert.AreEqual(1, outcomes.Count(x => x.Status == LogAdmissionStatus.RateLimited));
            await using var sql = new SqlConnection(database.ConnectionString);
            await sql.OpenAsync(token);
            await using (var tx = await sql.BeginTransactionAsync(token))
            {
                var log = await Vigiloom.Infrastructure.LogAdmission.LogAdmission.AdmitAsync(sql, tx, new(LogSourceKind.Application, source), 100, token);
                Assert.AreEqual(LogAdmissionStatus.Accepted, log.Status);
                await tx.CommitAsync(token);
            }
            await using var check = new LogAdmissionDbContext(options);
            Assert.AreEqual(2, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[TraceBuckets]").SingleAsync(token));
            Assert.AreEqual(2, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[Buckets]").SingleAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task TraceQuota_RejectsWholeBatchWithoutDebitingOtherServices() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var source = Guid.CreateVersion7(); var a = Guid.CreateVersion7(); var b = Guid.CreateVersion7();
            await using var sql = new SqlConnection(database.ConnectionString); await sql.OpenAsync(token);
            await using (var tx = await sql.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await TraceAdmission.AdmitAsync(sql, tx, [new(source, a, 1_000)], token)).Status);
                await tx.CommitAsync(token);
            }
            await using (var freeze = sql.CreateCommand())
            {
                freeze.CommandText = "UPDATE [logquota].[TraceBuckets] SET [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00')";
                await freeze.ExecuteNonQueryAsync(token);
            }
            await using (var tx = await sql.BeginTransactionAsync(token))
            {
                var denied = await TraceAdmission.AdmitAsync(sql, tx, [new(source, b, 1), new(source, a, 1)], token);
                Assert.AreEqual(LogAdmissionStatus.RateLimited, denied.Status);
                Assert.IsGreaterThan(TimeSpan.FromMinutes(59), denied.RetryAfter!.Value);
                await tx.CommitAsync(token);
            }
            await using (var check = sql.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM [logquota].[TraceBuckets]";
                Assert.AreEqual(2, Convert.ToInt32(await check.ExecuteScalarAsync(token)));
                check.CommandText = "SELECT [Microtokens] FROM [logquota].[TraceBuckets] WHERE [SourceId] = '00000000-0000-0000-0000-000000000000'";
                Assert.AreEqual(3_000_000_000L, Convert.ToInt64(await check.ExecuteScalarAsync(token)));
            }
            await using (var tx = await sql.BeginTransactionAsync(token))
                Assert.AreEqual(LogAdmissionStatus.BatchTooLarge, (await TraceAdmission.AdmitAsync(sql, tx, [new(source, b, 1_001)], token)).Status);
        }, context.CancellationToken);
}
