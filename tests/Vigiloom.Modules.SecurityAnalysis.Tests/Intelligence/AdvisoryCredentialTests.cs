using System.Data.Common;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AdvisoryCredentialTests(TestContext context)
{
    [TestMethod]
    public Task Migration_RefusesToDiscardProtectedCredential() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var protection = new EphemeralDataProtectionProvider();
            var encrypted = protection.CreateProtector(AdvisoryCredentials.ProtectionPurpose).Protect("ghp_test_token");
            db.IntelligenceSources.Add(new IntelligenceSource { Key = "github-reviewed", Enabled = true, ProtectedCredential = encrypted });
            await db.SaveChangesAsync(token);
            var previous = db.Database.GetMigrations().Single(x => x.EndsWith("_AdvisoryFailureStatus", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(previous, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(encrypted, (await db.IntelligenceSources.AsNoTracking().SingleAsync(token)).ProtectedCredential);
        }, context.CancellationToken);

    [TestMethod]
    public void SharedKeyRing_AllowsSeparateWebAndWorkerProvidersToReadCredential()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vigiloom-advisory-keys-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(path);
        try
        {
            static IDataProtectionProvider Provider(DirectoryInfo keys) => DataProtectionProvider.Create(keys,
                options => options.SetApplicationName("Vigiloom-test"));
            var encrypted = Provider(directory).CreateProtector(AdvisoryCredentials.ProtectionPurpose).Protect("ghp_test_token");
            Assert.AreEqual("ghp_test_token", Provider(directory).CreateProtector(AdvisoryCredentials.ProtectionPurpose).Unprotect(encrypted));
        }
        finally
        {
            var full = Path.GetFullPath(path);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(full), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(full).StartsWith("vigiloom-advisory-keys-", StringComparison.Ordinal) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to remove an unexpected key directory.");
            Directory.Delete(full, recursive: true);
        }
    }

    [TestMethod]
    public Task Credential_IsProtectedAuditedAndUsedOnlyForGitHubRequests() =>
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
            await using (var db = contexts.CreateDbContext()) { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            var protection = new EphemeralDataProtectionProvider();
            var actor = Guid.CreateVersion7();
            const string secret = "ghp_sensitive_test_token";
            var input = new AdvisoryCredentialUpdate(Convert.ToBase64String(source.Version), false, secret);
            var failing = new AdvisoryCredentials(contexts, new FailingAudit(), protection, TimeProvider.System);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.UpdateAsync(source.Id, input, actor, token));
            await using (var db = contexts.CreateDbContext()) Assert.IsNull((await db.IntelligenceSources.SingleAsync(token)).ProtectedCredential);
            var credentials = new AdvisoryCredentials(contexts, new OperationAuditWriter(), protection, TimeProvider.System);
            Assert.AreEqual(AdvisoryCredentialResult.Updated, await credentials.UpdateAsync(source.Id, input, actor, token));
            Assert.AreEqual(AdvisoryCredentialResult.Conflict, await credentials.UpdateAsync(source.Id, input, actor, token));
            await using (var db = contexts.CreateDbContext())
            {
                var saved = await db.IntelligenceSources.SingleAsync(token);
                Assert.IsNotNull(saved.ProtectedCredential);
                Assert.DoesNotContain(secret, saved.ProtectedCredential);
            }
            var status = await new IntelligenceStatus(contexts, TimeProvider.System).GetSourcesAsync(1, token);
            Assert.IsTrue(status.Items.Single().HasCredential);
            var calls = 0;
            var clients = new ClientFactory(request =>
            {
                calls++;
                Assert.AreEqual("api.github.com", request.RequestUri!.Host);
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                Assert.AreEqual(secret, request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            });
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await new AdvisoryRefresh(contexts, clients, TimeProvider.System, protection).RunDueAsync(token));
            Assert.AreEqual(4, calls);
            var audit = await new OperationAuditQueryService(new OperationsFactory(operationsOptions))
                .QueryIntelligenceAsync(new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.HasCount(1, audit.Items);
            Assert.AreEqual(actor, audit.Items[0].ActorId);
            Assert.IsTrue(audit.Items[0].AdvisoryCredential?.Configured);
            Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(audit));
            var current = (await new IntelligenceStatus(contexts, TimeProvider.System).GetSourcesAsync(1, token)).Items.Single();
            Assert.AreEqual(AdvisoryCredentialResult.Updated, await credentials.UpdateAsync(source.Id, new(current.Version, true, null), actor, token));
            Assert.IsFalse((await new IntelligenceStatus(contexts, TimeProvider.System).GetSourcesAsync(1, token)).Items.Single().HasCredential);
            var anonymousCalls = 0;
            var anonymousClients = new ClientFactory(request =>
            {
                anonymousCalls++;
                Assert.IsNull(request.Headers.Authorization);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            });
            Assert.AreEqual(AdvisoryRefreshResult.Staged, await new AdvisoryRefresh(contexts, anonymousClients, TimeProvider.System, protection).RunDueAsync(token));
            Assert.AreEqual(4, anonymousCalls);

            current = (await new IntelligenceStatus(contexts, TimeProvider.System).GetSourcesAsync(1, token)).Items.Single();
            Assert.AreEqual(AdvisoryCredentialResult.Updated, await credentials.UpdateAsync(source.Id, new(current.Version, false, secret), actor, token));
            var raceCalls = 0;
            var racingClients = new ClientFactory(request =>
            {
                raceCalls++;
                Assert.AreEqual(secret, request.Headers.Authorization?.Parameter);
                using var db = contexts.CreateDbContext();
                var version = Convert.ToBase64String(db.IntelligenceSources.Single().Version);
                Assert.AreEqual(AdvisoryCredentialResult.Updated,
                    credentials.UpdateAsync(source.Id, new(version, false, "ghp_replacement_token"), actor, token).GetAwaiter().GetResult());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            });
            Assert.AreEqual(AdvisoryRefreshResult.Conflict, await new AdvisoryRefresh(contexts, racingClients, TimeProvider.System, protection).RunDueAsync(token));
            Assert.AreEqual(1, raceCalls);
            await using (var db = contexts.CreateDbContext()) Assert.AreEqual(0, (await db.AdvisoryImports.SingleAsync(x => x.Phase < 4, token)).PageCount);
        }, context.CancellationToken);

    [TestMethod]
    public Task InvalidProtectedCredential_DoesNotFallBackToAnonymous() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext())
            {
                await db.Database.MigrateAsync(token);
                var originalProtection = new EphemeralDataProtectionProvider();
                var protectedValue = originalProtection.CreateProtector(AdvisoryCredentials.ProtectionPurpose).Protect("ghp_test_token");
                db.IntelligenceSources.Add(new IntelligenceSource { Key = "github-reviewed", Enabled = true, ProtectedCredential = protectedValue });
                await db.SaveChangesAsync(token);
            }
            var calls = 0;
            var clients = new ClientFactory(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });
            var result = await new AdvisoryRefresh(contexts, clients, TimeProvider.System, new EphemeralDataProtectionProvider()).RunDueAsync(token);
            Assert.AreEqual(AdvisoryRefreshResult.Failed, result);
            Assert.AreEqual(0, calls);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual("credential-unavailable", (await check.AdvisoryImports.SingleAsync(token)).FailureCode);
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
    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
}
