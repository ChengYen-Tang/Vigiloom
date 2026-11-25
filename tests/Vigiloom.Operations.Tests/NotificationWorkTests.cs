using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Operations.Notifications;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NotificationWorkTests(TestContext context)
{
    [TestMethod]
    public Task RetryDoesNotDuplicateNotificationAndChangedDeliveryIsRejected() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
            await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Operations"] = database.ConnectionString }).Build();
            var handler = new NotificationWorkHandler(config, new NotificationWriter());
            var notification = new NotificationRequest(Guid.CreateVersion7(), "ping", DateTimeOffset.UtcNow, "Ping unreachable", "Device: Router");
            var work = new ServiceWork(NotificationWork.Kind, JsonSerializer.Serialize(notification), Guid.CreateVersion7().ToString("D"));
            await handler.ExecuteAsync(work, token);
            await handler.ExecuteAsync(work, token);
            await handler.ExecuteAsync(work with { DeliveryId = Guid.CreateVersion7().ToString("D") }, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(work with { Payload = JsonSerializer.Serialize(notification with { Detail = "changed" }) }, token));
            await using var result = await factory.CreateDbContextAsync(token);
            var stored = await result.Set<Notification>().SingleAsync(token);
            Assert.AreEqual(notification.MessageId, stored.MessageId);
            Assert.AreEqual(notification.Detail, stored.Detail);
        }, context.CancellationToken);
}
