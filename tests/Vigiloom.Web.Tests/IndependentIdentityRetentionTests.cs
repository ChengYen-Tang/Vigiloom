extern alias IdentityHost;
extern alias WorkerHostAssembly;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IndependentIdentityRetentionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task RemoteRetention_UsesIdentityDatabaseAndPreservesInitializationMarker() =>
        ReusableWebDatabase.ExecuteAsync(async (platform, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (identityDatabase, token) =>
        {
            using var identity = new WebApplicationFactory<IdentityHost::Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:VigiloomIdentity"] = identityDatabase.ConnectionString
                })));
            var identityContexts = identity.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            var accountId = Guid.CreateVersion7();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            await using (var db = await identityContexts.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                db.AccountAuditEvents.Add(new AccountAuditEvent(null, accountId, "InitialAdministratorCreated", cutoff.AddDays(-1)));
                db.AccountAuditEvents.Add(new AccountAuditEvent(accountId, accountId, "AccountCreated", cutoff.AddDays(-1)));
                db.ApiCredentialAuditEvents.Add(new ApiCredentialAuditEvent(accountId, Guid.CreateVersion7(), "ApiCredentialIssued", cutoff.AddDays(-1)));
                await db.SaveChangesAsync(token);
            }
            using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = platform.ConnectionString,
                    ["Services:Identity:Url"] = "http://identity"
                }));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteIdentityAuditRetention))
                    .ConfigurePrimaryHttpMessageHandler(() => identity.Server.CreateHandler()));
            });
            await using var scope = web.Services.CreateAsyncScope();
            var source = scope.ServiceProvider.GetServices<IAuditRetentionSource>().OfType<RemoteIdentityAuditRetention>().Single();
            var workerBuilder = WorkerHostAssembly::Vigiloom.Worker.Hosting.WorkerHost.CreateBuilder([]);
            workerBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Services:Identity:Url"] = "http://identity" });
            using (var worker = workerBuilder.Build())
            using (var workerScope = worker.Services.CreateScope())
                Assert.HasCount(1, workerScope.ServiceProvider.GetServices<IAuditRetentionSource>().OfType<RemoteIdentityAuditRetention>());
            var operations = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using var operationDb = await operations.CreateDbContextAsync(token);
            await using var transaction = await operationDb.Database.BeginTransactionAsync(token);
            Assert.AreEqual(2L, await source.CountAsync(operationDb.Database.GetDbConnection(), transaction.GetDbTransaction(), cutoff, token));
            Assert.AreEqual(2, await source.DeleteBatchAsync(operationDb.Database.GetDbConnection(), transaction.GetDbTransaction(), cutoff, 500, token));
            await transaction.RollbackAsync(token);
            await using var verify = await identityContexts.CreateDbContextAsync(token);
            Assert.AreEqual(1, await verify.AccountAuditEvents.CountAsync(token));
            Assert.AreEqual("InitialAdministratorCreated", (await verify.AccountAuditEvents.SingleAsync(token)).Action);
            Assert.AreEqual(0, await verify.ApiCredentialAuditEvents.CountAsync(token));
            verify.AccountAuditEvents.Add(new AccountAuditEvent(accountId, accountId, "PasswordChanged", DateTimeOffset.UtcNow.AddDays(-400)));
            await verify.SaveChangesAsync(token);
            var settings = scope.ServiceProvider.GetRequiredService<AuditRetentionSettings>();
            var policy = await settings.GetAsync(token);
            var preview = await settings.PreviewAsync(new AuditRetentionPreviewRequest(policy.Days, policy.Version), accountId, token);
            Assert.IsNotNull(preview);
            Assert.IsGreaterThanOrEqualTo(1L, preview.AffectedRecords);
            var cleanup = new AuditRetentionCleanup(operations, settings, scope.ServiceProvider.GetServices<IAuditRetentionSource>());
            Assert.IsGreaterThanOrEqualTo(1L, await cleanup.RunAsync(token));
            Assert.AreEqual(1, await verify.AccountAuditEvents.CountAsync(token));
        }, outerToken), TestContext.CancellationToken);
}
