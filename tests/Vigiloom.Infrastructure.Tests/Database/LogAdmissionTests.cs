using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;
using Admission = Vigiloom.Infrastructure.LogAdmission.LogAdmission;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LogAdmissionTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Admission_WholeRequestRejectsWithoutDebitingPrefixAndCanRetry() =>
        ReusableLogQuotaDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var source = new LogSource(LogSourceKind.Application, Guid.CreateVersion7());
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 1, token)).Status);
                await tx.CommitAsync(token);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE [logquota].[Buckets] SET [Microtokens] = 60000000, [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00')";
                await command.ExecuteNonQueryAsync(token);
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                var denied = await Admission.AdmitAsync(connection, tx, source, 60 + 40, token);
                Assert.AreEqual(LogAdmissionStatus.RateLimited, denied.Status);
                Assert.IsNotNull(denied.RetryAfter);
                await tx.CommitAsync(token);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM [logquota].[Buckets] WHERE [Microtokens] = 60000000";
                Assert.AreEqual(2, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "UPDATE [logquota].[Buckets] SET [Microtokens] = 100000000";
                await command.ExecuteNonQueryAsync(token);
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 100, token)).Status);
                await tx.CommitAsync(token);
            }
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Policy_ConcurrentUpdatesHaveOneWinner(bool existingPolicy) =>
        ReusableLogQuotaDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var admin = new PolicyAdministration(new PooledDbContextFactory<LogAdmissionDbContext>(options));
            var actor = Guid.CreateVersion7();
            if (existingPolicy)
                Assert.IsNotNull(await admin.UpdateAsync(LogQuotaValues.Default, "", actor, (_, _, _, _) => Task.CompletedTask, token));
            var current = await admin.GetAsync(token);
            var left = new LogQuotaValues(22, 100, 1000, 2000);
            var right = new LogQuotaValues(33, 100, 1000, 2000);
            var results = await Task.WhenAll(
                admin.UpdateAsync(left, current.Version, actor, (_, _, _, _) => Task.CompletedTask, token),
                admin.UpdateAsync(right, current.Version, actor, (_, _, _, _) => Task.CompletedTask, token));
            Assert.AreEqual(1, results.Count(x => x is not null));
            Assert.AreEqual(results.Single(x => x is not null), await admin.GetAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Policy_RealAuditAndSettingsCommitOrRollBackTogether() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var quotaOptions = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            var auditOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new LogAdmissionDbContext(quotaOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(auditOptions)) await db.Database.MigrateAsync(token);
            var admin = new PolicyAdministration(new PooledDbContextFactory<LogAdmissionDbContext>(quotaOptions));
            var writer = new OperationAuditWriter();
            var actor = Guid.CreateVersion7();
            async Task Append(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction tx, LogQuotaChange change, CancellationToken ct)
            {
                static LogQuotaAuditValues Map(LogQuotaValues value) => new(value.SourceRate, value.SourceBurst, value.GlobalRate, value.GlobalBurst);
                await writer.AppendAsync(connection, tx, new(actor, change.PolicyId, change.OccurredAt,
                    new LogQuotaChanged(Map(change.Previous.Values), Map(change.Current.Values), change.Previous.Version, change.Current.Version)), ct);
            }
            var values = new LogQuotaValues(30, 120, 1500, 3000);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => admin.UpdateAsync(values, "", actor, async (connection, tx, change, ct) =>
            {
                await Append(connection, tx, change, ct);
                throw new InvalidOperationException("Simulated failure after audit write");
            }, token));
            await using (var db = new OperationsDbContext(auditOptions))
                Assert.AreEqual(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [operations].[AuditEvents]").SingleAsync(token));
            Assert.AreEqual(LogQuotaValues.Default, (await admin.GetAsync(token)).Values);
            var saved = await admin.UpdateAsync(values, "", actor, Append, token);
            Assert.IsNotNull(saved);
            var query = new OperationAuditQueryService(new PooledDbContextFactory<OperationsDbContext>(auditOptions));
            var now = DateTimeOffset.UtcNow;
            var entries = await query.QueryLogQuotaAsync(new(now.AddMinutes(-5), now.AddMinutes(5)), token);
            Assert.HasCount(1, entries.Items);
            Assert.AreEqual(30, entries.Items[0].LogQuota!.Current.SourceRate);
            Assert.AreEqual(saved.Version, entries.Items[0].LogQuota!.CurrentVersion);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Policy_EffectiveTimeFollowsEarlierAdmissionCommit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var admin = new PolicyAdministration(new PooledDbContextFactory<LogAdmissionDbContext>(options));
            var actor = Guid.CreateVersion7();
            var current = await admin.UpdateAsync(LogQuotaValues.Default, "", actor, (_, _, _, _) => Task.CompletedTask, token);
            Assert.IsNotNull(current);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var tx = await connection.BeginTransactionAsync(token);
            Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx,
                new(LogSourceKind.Agent, Guid.CreateVersion7()), 1, token)).Status);
            LogQuotaChange? recorded = null;
            var pending = admin.UpdateAsync(new(1000, 100, 1000, 2000), current.Version, actor,
                (_, _, change, _) => { recorded = change; return Task.CompletedTask; }, token);
            DateTimeOffset beforeRelease;
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqlTransaction)tx;
                command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @@SPID";
                var blocked = false;
                for (var i = 0; i < 100 && !blocked; i++)
                {
                    blocked = Convert.ToInt32(await command.ExecuteScalarAsync(token)) > 0;
                    if (!blocked) await Task.Delay(50, token);
                }
                Assert.IsTrue(blocked, "Policy update must reach the SQL lock and wait for admission.");
                command.CommandText = "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
                beforeRelease = (DateTimeOffset)(await command.ExecuteScalarAsync(token))!;
                await tx.CommitAsync(token);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                try { await pending; } catch { /* Preserve the original test failure. */ }
                throw;
            }
            Assert.IsNotNull(await pending);
            Assert.IsNotNull(recorded);
            Assert.IsGreaterThanOrEqualTo(beforeRelease, recorded.OccurredAt);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Policy_UpdateIsVersionedAndRollsBackWhenAuditFails() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var admin = new PolicyAdministration(new PooledDbContextFactory<LogAdmissionDbContext>(options));
            var actor = Guid.CreateVersion7();
            var original = await admin.GetAsync(token);
            Assert.AreEqual(LogQuotaValues.Default, original.Values);
            var desired = new LogQuotaValues(30, 120, 1500, 3000);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => admin.UpdateAsync(desired, original.Version, actor,
                (_, _, _, _) => throw new InvalidOperationException("Audit failed"), token));
            Assert.AreEqual(original, await admin.GetAsync(token));
            LogQuotaChange? recorded = null;
            var saved = await admin.UpdateAsync(desired, original.Version, actor,
                (_, _, change, _) => { recorded = change; return Task.CompletedTask; }, token);
            Assert.IsNotNull(saved);
            Assert.AreEqual(desired, saved.Values);
            Assert.HasCount(8, Convert.FromBase64String(saved.Version));
            Assert.IsNotNull(recorded);
            Assert.AreEqual(original, recorded.Previous);
            Assert.AreEqual(saved, recorded.Current);
            Assert.AreEqual(TimeSpan.Zero, recorded.OccurredAt.Offset);
            Assert.IsNull(await admin.UpdateAsync(LogQuotaValues.Default, original.Version, actor,
                (_, _, _, _) => throw new AssertFailedException("A stale update must not be audited"), token));
            Assert.AreEqual(saved, await admin.GetAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Admission_AgentAndApplicationShareAtomicGlobalCapacity(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
                var id = Guid.CreateVersion7();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [logquota].[Policies] ([Id],[Name],[SourceRate],[SourceBurst],[GlobalRate],[GlobalBurst],[CreatedAt],[UpdatedAt]) VALUES ({id},{"central"},{1},{200},{1},{200},TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
            }
            async Task<LogAdmissionResult> Admit(LogSource source, int count, bool commit = true)
            {
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var tx = await connection.BeginTransactionAsync(token);
                var result = await Admission.AdmitAsync(connection, tx, source, count, token);
                if (commit) await tx.CommitAsync(token);
                return result;
            }
            var agent = new LogSource(LogSourceKind.Agent, Guid.CreateVersion7());
            var app = new LogSource(LogSourceKind.Application, Guid.CreateVersion7());
            var results = await Task.WhenAll(Admit(agent, 200), Admit(app, 200));
            Assert.AreEqual(1, results.Count(x => x.Status == LogAdmissionStatus.Accepted));
            Assert.AreEqual(1, results.Count(x => x.Status == LogAdmissionStatus.RateLimited));
            Assert.IsNotNull(results.Single(x => x.Status == LogAdmissionStatus.RateLimited).RetryAfter);
            await using var check = new LogAdmissionDbContext(options);
            var rows = await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [logquota].[Buckets]").SingleAsync(token);
            Assert.AreEqual(2, rows, "A rejected source must not create or debit a bucket.");
            Assert.AreEqual(LogAdmissionStatus.BatchTooLarge, (await Admit(agent, 201)).Status);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Admission_RollbackDoesNotConsumeCapacity() =>
        ReusableLogQuotaDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var source = new LogSource(LogSourceKind.Agent, Guid.CreateVersion7());
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 100, token)).Status);
                await tx.RollbackAsync(token);
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 100, token)).Status);
                await tx.CommitAsync(token);
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
                Assert.AreEqual(LogAdmissionStatus.RateLimited, (await Admission.AdmitAsync(connection, tx, source, 100, token)).Status);
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Admission_ClockRollbackDoesNotRefillOrMoveStoredTimeBackwards() =>
        ReusableLogQuotaDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var source = new LogSource(LogSourceKind.Agent, Guid.CreateVersion7());
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 1, token)).Status);
                await tx.CommitAsync(token);
            }
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE [logquota].[Buckets] SET [Microtokens] = 2000000, [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00')";
                await update.ExecuteNonQueryAsync(token);
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 1, token)).Status);
                await tx.CommitAsync(token);
            }
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM [logquota].[Buckets] WHERE [Microtokens] = 1000000 AND [LastRefill] > TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')";
                Assert.AreEqual(2, Convert.ToInt32(await check.ExecuteScalarAsync(token)));
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                var denied = await Admission.AdmitAsync(connection, tx, source, 2, token);
                Assert.AreEqual(LogAdmissionStatus.RateLimited, denied.Status);
                Assert.IsGreaterThan(TimeSpan.FromMinutes(59), denied.RetryAfter!.Value);
            }
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Admission_PolicyChangeDoesNotRefillUsingOldElapsedTime() =>
        ReusableLogQuotaDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var source = new LogSource(LogSourceKind.Application, Guid.CreateVersion7());
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 1, token)).Status);
                await tx.CommitAsync(token);
            }
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE [logquota].[Buckets] SET [Microtokens] = 0, [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, -1, SYSUTCDATETIME()), '+00:00'); INSERT INTO [logquota].[Policies] ([Id],[Name],[SourceRate],[SourceBurst],[GlobalRate],[GlobalBurst],[CreatedAt],[UpdatedAt]) VALUES (@id,N'central',1,100,1,100,TODATETIMEOFFSET(DATEADD(second,-5,SYSUTCDATETIME()), '+00:00'),TODATETIMEOFFSET(DATEADD(second,-5,SYSUTCDATETIME()), '+00:00'))";
                update.Parameters.AddWithValue("@id", Guid.CreateVersion7());
                await update.ExecuteNonQueryAsync(token);
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
                Assert.AreEqual(LogAdmissionStatus.RateLimited, (await Admission.AdmitAsync(connection, tx, source, 50, token)).Status);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 1, token)).Status);
                await tx.CommitAsync(token);
            }
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Admission_SourceExhaustionDoesNotDebitGlobalCapacity() =>
        ReusableLogQuotaDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
            var source = new LogSource(LogSourceKind.Agent, Guid.CreateVersion7());
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(LogAdmissionStatus.Accepted, (await Admission.AdmitAsync(connection, tx, source, 100, token)).Status);
                await tx.CommitAsync(token);
            }
            await using (var freeze = connection.CreateCommand())
            {
                freeze.CommandText = "UPDATE [logquota].[Buckets] SET [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00')";
                await freeze.ExecuteNonQueryAsync(token);
            }
            async Task<long> GlobalTokens()
            {
                await using var query = connection.CreateCommand();
                query.CommandText = "SELECT [Microtokens] FROM [logquota].[Buckets] WHERE [Kind] = 0";
                return Convert.ToInt64(await query.ExecuteScalarAsync(token));
            }
            var before = await GlobalTokens();
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                var denied = await Admission.AdmitAsync(connection, tx, source, 1, token);
                Assert.AreEqual(LogAdmissionStatus.RateLimited, denied.Status);
                await tx.CommitAsync(token);
            }
            Assert.AreEqual(before, await GlobalTokens());
        }, context.CancellationToken);
}
