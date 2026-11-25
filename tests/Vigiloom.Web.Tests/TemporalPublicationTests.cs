using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemporalPublicationTests(TestContext context)
{
    [TestMethod]
    public Task PublicationDeliveryIsAppliedOnceAndRejectsConflictingPayload() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(host, token);
            await using (var scheduling = await host.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
                await scheduling.Database.MigrateAsync(token);
            var contexts = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            var source = new ServiceSource("source", new byte[32], DateTimeOffset.UtcNow);
            var serviceId = Guid.CreateVersion7();
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source);
                db.Services.Add(MonitoredService.FromResource(serviceId, source.Id, "orders", "production", "Orders", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(token);
            }
            await using var scope = host.Services.CreateAsyncScope();
            var projection = scope.ServiceProvider.GetRequiredService<ApplicationStateProjection>();
            var delivery = Guid.CreateVersion7();
            var hash = SHA256.HashData([1]);
            await projection.InvalidatePublicationAsync(serviceId, token, delivery, hash);
            await projection.InvalidatePublicationAsync(serviceId, token, delivery, hash);
            await Assert.ThrowsAsync<InvalidOperationException>(() => projection.InvalidatePublicationAsync(serviceId, token, delivery, SHA256.HashData([2])));
            await using var verify = await host.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await verify.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM scheduling.WorkReceipts WHERE MessageId = {delivery}").SingleAsync(token));
        }, context.CancellationToken);
}
