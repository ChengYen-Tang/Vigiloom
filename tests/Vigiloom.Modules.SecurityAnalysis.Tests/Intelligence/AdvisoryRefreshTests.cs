using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryRefreshTests(TestContext context)
{
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();
    [TestMethod]
    public Task RunDue_RecordsInterruptedResponseWithoutAdvancingImport() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clients = new ClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenResponseStream()) });
            var refresh = new AdvisoryRefresh(contexts, clients, TimeProvider.System, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.Failed, await refresh.RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            var import = await db.AdvisoryImports.SingleAsync(token);
            Assert.AreEqual("fetch-failed", import.FailureCode);
            Assert.AreEqual(0, import.PageCount);
            Assert.IsNull(import.Cursor);
            Assert.AreEqual(0, await db.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task RevokedProvenance_RebuildsFullWindowWithoutReusingRevokedEntries(bool revokeAncestor) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operationOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operationOptions)) await db.Database.MigrateAsync(token);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var clients = new ClientFactory(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Query.Contains("is_withdrawn=true", StringComparison.Ordinal) ? "[]" :
                    """[{"ghsa_id":"GHSA-abcd-1234-efgh","type":"reviewed","published_at":"2023-01-01T00:00:00Z","updated_at":"2023-01-03T00:00:00Z","withdrawn_at":null,"vulnerabilities":[]}]""", Encoding.UTF8, "application/json")
            });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Guid sourceId;
            Guid firstId;
            await using (var db = contexts.CreateDbContext())
            {
                var import = await db.AdvisoryImports.SingleAsync(token);
                sourceId = import.SourceId; firstId = import.ResultRevisionId!.Value;
            }
            var actor = Guid.CreateVersion7();
            var publisher = new IntelligencePublication(contexts, new OperationAuditWriter(), clock);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(sourceId, firstId, null, "", false, actor, token));
            var activeId = firstId;
            if (revokeAncestor)
            {
                clock.Advance(TimeSpan.FromDays(2));
                Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
                await using var db = contexts.CreateDbContext();
                var second = await db.AdvisoryImports.SingleAsync(x => x.BaseRevisionId == firstId && x.Phase == 4, token);
                var initialVersion = Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
                activeId = second.ResultRevisionId!.Value;
                Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(sourceId, activeId, firstId, initialVersion, false, actor, token));
            }
            Assert.AreEqual(IntelligenceRevocationResult.Revoked, await new IntelligenceRevocation(contexts, new OperationAuditWriter(), clock)
                .RevokeAsync(sourceId, firstId, actor, token));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Guid replacementId;
            string version;
            await using (var db = contexts.CreateDbContext())
            {
                var rebuilt = await db.AdvisoryImports.SingleAsync(x => x.BaseRevisionId == activeId && x.ResultRevisionId != null && x.Phase == 4, token);
                Assert.AreEqual(new DateOnly(1970, 1, 1), rebuilt.WindowStart);
                Assert.AreEqual(activeId, rebuilt.BaseRevisionId);
                replacementId = rebuilt.ResultRevisionId!.Value;
                var origins = await (from member in db.AdvisoryRevisionEntries join entry in db.AdvisoryImportEntries on member.EntryRowId equals entry.RowId
                    where member.RevisionId == replacementId select entry.ImportId).ToArrayAsync(token);
                Assert.IsNotEmpty(origins);
                Assert.IsTrue(origins.All(id => id == rebuilt.Id));
                version = Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
            }
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(sourceId, replacementId, activeId, version, false, actor, token));
            await using var final = contexts.CreateDbContext();
            Assert.IsTrue((await final.IntelligenceRevisions.SingleAsync(x => x.Id == firstId, token)).Revoked);
            Assert.AreEqual(replacementId, (await final.IntelligenceActivations.SingleAsync(token)).RevisionId);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_ConflictingBaseContentCanBeRecovered() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operationOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operationOptions)) await db.Database.MigrateAsync(token);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var changed = false;
            var clients = new ClientFactory(request =>
            {
                var payload = request.RequestUri!.Query.Contains("is_withdrawn=true", StringComparison.Ordinal) ? "[]" :
                    $$"""[{"ghsa_id":"GHSA-abcd-1234-efgh","type":"reviewed","published_at":"2023-01-01T00:00:00Z","updated_at":"2023-01-03T00:00:00Z","withdrawn_at":null,"summary":"{{(changed ? "changed" : "original")}}","vulnerabilities":[]}]""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Guid sourceId;
            Guid activeId;
            await using (var db = contexts.CreateDbContext())
            {
                var initial = await db.AdvisoryImports.SingleAsync(token);
                sourceId = initial.SourceId;
                activeId = initial.ResultRevisionId!.Value;
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = sourceId, RevisionId = activeId, ActivatedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(token);
            }
            changed = true;
            clock.Advance(TimeSpan.FromDays(2));
            Assert.AreEqual(AdvisoryRefreshResult.Failed, await refresh.RunDueAsync(token));
            var status = new IntelligenceStatus(contexts, clock);
            var failed = await status.GetAdvisoryProgressAsync(sourceId, token);
            Assert.IsNotNull(failed);
            Assert.AreEqual("failed", failed.State);
            Assert.AreEqual("invalid-response", failed.FailureCode);
            Assert.AreEqual(AdvisoryRefreshResult.Failed, await refresh.RunDueAsync(token));
            var recovery = new AdvisoryRecovery(contexts, new OperationAuditWriter(), clock);
            Assert.AreEqual(AdvisoryRecoveryResult.Abandoned, await recovery.AbandonAsync(sourceId,
                new AdvisoryImportRecoveryRequest(failed.ImportId!.Value, failed.Version!), Guid.CreateVersion7(), token));
            changed = false;
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(activeId, (await check.IntelligenceActivations.SingleAsync(token)).RevisionId);
            Assert.AreEqual(2, await check.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);
    [TestMethod]
    public Task Progress_WaitsForSourceWriterBeforeReadingImport() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var refresh = new AdvisoryRefresh(contexts, new ClientFactory(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)), clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Guid sourceId;
            await using (var db = contexts.CreateDbContext()) sourceId = (await db.IntelligenceSources.SingleAsync(token)).Id;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @id";
                command.Parameters.AddWithValue("@id", sourceId);
                await command.ExecuteScalarAsync(token);
                command.CommandText = "UPDATE [security].[AdvisoryImports] SET [PageCount] = [PageCount] WHERE [SourceId] = @id";
                await command.ExecuteNonQueryAsync(token);
            }
            var status = new IntelligenceStatus(contexts, clock);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var read = status.GetAdvisoryProgressAsync(sourceId, deadline.Token);
            try
            {
                await using (var observer = new SqlConnection(database.ConnectionString))
                {
                    await observer.OpenAsync(deadline.Token);
                    await using var command = observer.CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE [blocking_session_id] = @blocker";
                    command.Parameters.AddWithValue("@blocker", connection.ServerProcessId);
                    while ((int)(await command.ExecuteScalarAsync(deadline.Token))! == 0)
                        await Task.Delay(50, deadline.Token);
                }
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE [security].[IntelligenceSources] SET [LastError] = 'test' WHERE [Id] = @id";
                    command.Parameters.AddWithValue("@id", sourceId);
                    await command.ExecuteNonQueryAsync(deadline.Token);
                }
                await transaction.CommitAsync(deadline.Token);
                Assert.AreEqual("rate-limited", (await read.WaitAsync(deadline.Token))?.State);
            }
            finally
            {
                await deadline.CancelAsync();
                if (transaction.Connection is not null)
                {
                    try { await transaction.RollbackAsync(CancellationToken.None); }
                    catch (Exception error) when (error is InvalidOperationException or SqlException) { }
                }
                try { await read; }
                catch (Exception) { }
            }
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_ReportsSanitizedFailureAndClearsItAfterRecovery() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ => ++calls switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("sensitive upstream data") },
                2 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") }
            });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.Failed, await refresh.RunDueAsync(token));
            Guid sourceId;
            await using (var db = contexts.CreateDbContext()) sourceId = (await db.IntelligenceSources.SingleAsync(token)).Id;
            var status = new IntelligenceStatus(contexts, clock);
            var failed = await status.GetAdvisoryProgressAsync(sourceId, token);
            Assert.IsNotNull(failed);
            Assert.AreEqual("failed", failed.State);
            Assert.AreEqual("fetch-failed", failed.FailureCode);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            var limited = await status.GetAdvisoryProgressAsync(sourceId, token);
            Assert.IsNotNull(limited);
            Assert.AreEqual("rate-limited", limited.State);
            Assert.IsNotNull(limited.RetryAt);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            var recovered = await status.GetAdvisoryProgressAsync(sourceId, token);
            Assert.IsNotNull(recovered);
            Assert.AreEqual("awaiting-activation", recovered.State);
            Assert.IsNull(recovered.FailureCode);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_ResumesRateLimitedImportAndWaitsForActivation() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ => ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual(5, calls);
            Assert.AreEqual(AdvisoryRefreshResult.AwaitingActivation, await refresh.RunDueAsync(token));
            Assert.AreEqual(5, calls);
            await using (var check = contexts.CreateDbContext())
            {
                var import = await check.AdvisoryImports.SingleAsync(token);
                var source = await check.IntelligenceSources.SingleAsync(token);
                Assert.AreEqual(1_073_741_824, source.MaximumBytes);
                Assert.AreEqual(100_000, source.MaximumRecords);
                Assert.AreEqual(new DateOnly(1970, 1, 1), import.WindowStart);
                Assert.AreEqual(new DateOnly(2023, 2, 28), import.WindowEnd);
                Assert.IsNotNull(import.ResultRevisionId);
                Assert.AreEqual(0, await check.IntelligenceActivations.CountAsync(token));
            }
            clock.Advance(TimeSpan.FromDays(4));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual(9, calls);
            await using (var check = contexts.CreateDbContext()) Assert.AreEqual(2, await check.IntelligenceRevisions.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_UsesOverlapFromActivatedRevision() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var clients = new ClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                var first = await db.AdvisoryImports.SingleAsync(token);
                Assert.IsNotNull(first.ResultRevisionId);
                db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = first.SourceId, RevisionId = first.ResultRevisionId.Value, ActivatedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(token);
            }
            clock.Advance(TimeSpan.FromDays(2));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using var check = contexts.CreateDbContext();
            var latest = await check.AdvisoryImports.OrderByDescending(x => x.StartedAt).FirstAsync(token);
            Assert.AreEqual(new DateOnly(2023, 2, 21), latest.WindowStart);
            Assert.AreEqual(new DateOnly(2023, 3, 2), latest.WindowEnd);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_RefreshesAfterCandidatePolicyChanges() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") }; });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceSources.SingleAsync(token)).UpdateIntervalSeconds = 300;
                await db.SaveChangesAsync(token);
            }
            clock.Advance(TimeSpan.FromMinutes(11));
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await refresh.RunDueAsync(token));
            Assert.AreEqual(8, calls);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_PolicyChangesDuringRequestStillPreserveRateLimit() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ =>
            {
                calls++;
                if (calls == 1)
                {
                    using var db = contexts.CreateDbContext();
                    db.IntelligenceSources.Single().UpdateIntervalSeconds = 43_200;
                    db.SaveChanges();
                }
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return response;
            });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(AdvisoryRefreshResult.Deferred, await refresh.RunDueAsync(token));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
            clock.Advance(TimeSpan.FromMinutes(61));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(2, calls);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_LateLimitOnAbandonedImportBlocksNewGeneration() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.TooManyRequests); });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            var store = new AdvisoryImportStore(contexts, clock);
            AdvisoryImport old;
            await using (var db = contexts.CreateDbContext()) old = await db.AdvisoryImports.AsNoTracking().SingleAsync(token);
            Assert.IsTrue(await store.AbandonAsync(old.SourceId, old.Id, old.Version, token));
            clock.Advance(TimeSpan.FromMinutes(2));
            var next = await store.BeginAsync(old.SourceId, old.WindowStart, old.WindowEnd, token);
            Assert.IsNotNull(next);
            Assert.IsTrue(await store.DeferAsync(old.SourceId, old.Id, clock.GetUtcNow().AddHours(1), token));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
        }, context.CancellationToken);

    [TestMethod]
    public Task RunDue_PolicyChangeAbandonsStaleImportBeforeRetry() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var clients = new ClientFactory(_ =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return response;
            });
            var refresh = new AdvisoryRefresh(contexts, clients, clock, Protection);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext())
            {
                (await db.IntelligenceSources.SingleAsync(token)).UpdateIntervalSeconds = 43_200;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(AdvisoryRefreshResult.Deferred, await refresh.RunDueAsync(token));
            await using (var db = contexts.CreateDbContext()) Assert.AreEqual(5, (await db.AdvisoryImports.SingleAsync(token)).Phase);
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, calls);
            clock.Advance(TimeSpan.FromMinutes(61));
            Assert.AreEqual(AdvisoryRefreshResult.RateLimited, await refresh.RunDueAsync(token));
            Assert.AreEqual(2, calls);
        }, context.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
