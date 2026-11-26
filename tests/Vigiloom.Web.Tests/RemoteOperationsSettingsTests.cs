extern alias OperationsHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.LogQuota;
using Vigiloom.Web.Operations;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RemoteOperationsSettingsTests(TestContext context)
{
    [TestMethod]
    public Task WebUpdatesOperationsRetentionAndSharedLogQuota() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (legacy, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operations, token) =>
        {
            var opsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operations.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operations.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            var quotaOptions = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(operations.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
            await using (var db = new OperationsDbContext(opsOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new LogAdmissionDbContext(quotaOptions)) await db.Database.MigrateAsync(token);
            using var operationsHost = new WebApplicationFactory<OperationsHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:EnableOperationsWorker", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operations.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    foreach (var item in services.Where(x => x.ImplementationType?.Name is "WorkDispatcher" or "LineAggregationWorker").ToArray()) services.Remove(item);
                });
            });
            using var operationsClient = operationsHost.CreateClient();
            using var webHost = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Vigiloom"] = legacy.ConnectionString, ["Services:Operations:Url"] = "http://operations" }));
                web.ConfigureTestServices(services =>
                {
                    services.AddHttpClient(nameof(AuditRetentionAccess)).ConfigurePrimaryHttpMessageHandler(() => operationsHost.Server.CreateHandler());
                    services.AddHttpClient(nameof(LogQuotaReader)).ConfigurePrimaryHttpMessageHandler(() => operationsHost.Server.CreateHandler());
                });
            });
            await using var scope = webHost.Services.CreateAsyncScope();
            var retention = scope.ServiceProvider.GetRequiredService<AuditRetentionAccess>();
            var actor = Guid.CreateVersion7();
            var current = await retention.GetAsync(token);
            var preview = await retention.PreviewAsync(new AuditRetentionPreviewRequest(30, current.Version), actor, token);
            Assert.IsNotNull(preview);
            var saved = await retention.UpdateAsync(new AuditRetentionInput(30, current.Version, preview.ConfirmationToken), actor, token);
            Assert.AreEqual(30, saved!.Days);
            Assert.AreEqual(30, (await retention.GetAsync(token)).Days);
            Assert.IsNull(await retention.PreviewAsync(new AuditRetentionPreviewRequest(30, current.Version), actor, token));

            var quota = scope.ServiceProvider.GetRequiredService<LogQuotaReader>();
            var original = await quota.GetSnapshotAsync(token);
            var values = new LogQuotaValues(20, 100, 200, 500);
            var changed = await quota.UpdateAsync(values, original.Version, actor, token);
            Assert.AreEqual(values, changed!.Values);
            Assert.AreEqual(values, (await quota.GetSnapshotAsync(token)).Values);
            Assert.IsNull(await quota.UpdateAsync(values, original.Version, actor, token));
            await using var verify = new OperationsDbContext(opsOptions);
            Assert.AreEqual(30, await verify.Database.SqlQueryRaw<int>("SELECT Days AS [Value] FROM operations.AuditRetentionSettings WHERE Slot = 1").SingleAsync(token));
        }, outerToken), context.CancellationToken);
}
