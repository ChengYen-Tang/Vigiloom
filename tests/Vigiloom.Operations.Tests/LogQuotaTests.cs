using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LogQuotaTests(TestContext context)
{
    [TestMethod]
    public Task SharedQuotaUpdatesAndAuditsInOperationsDatabase() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<LogAdmissionDbContext>(database.ConnectionString, "logquota");
            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = database.ConnectionString })));
            using var client = host.CreateClient();
            var initial = await client.GetFromJsonAsync<LogQuotaSettings>("/internal/v1/log-quota", token);
            Assert.IsNotNull(initial);
            var update = new LogQuotaUpdate(initial with { SourceRate = 30 }, Guid.CreateVersion7());
            using var saved = await client.PutAsJsonAsync("/internal/v1/log-quota", update, token);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var current = await saved.Content.ReadFromJsonAsync<LogQuotaSettings>(cancellationToken: token);
            Assert.AreEqual(30, current?.SourceRate);
            using var stale = await client.PutAsJsonAsync("/internal/v1/log-quota", update, token);
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            await using var verify = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await verify.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.AuditEvents").SingleAsync(token));
        }, context.CancellationToken);
}
