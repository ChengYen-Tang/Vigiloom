using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Worker.Hosting;

namespace Vigiloom.Worker.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemporalNotificationWriterTests(TestContext context)
{
    [TestMethod]
    public void OperationsModeResolvesOutboxHandlersWithoutCircularDependencies()
    {
        var builder = WorkerHost.CreateBuilder(["--Services:Operations:Url=http://operations", "--ConnectionStrings:Vigiloom=Server=localhost;Database=unused;Integrated Security=true;Encrypt=false"]);
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToArray();
        Assert.IsTrue(handlers.Any(handler => handler.MessageType == TemporalOutboxHandler.Type));
    }

    [TestMethod]
    public Task SecurityNotificationSharesSourceTransaction() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
        var type = typeof(WorkerHost).Assembly.GetType("Vigiloom.Worker.Hosting.TemporalNotificationWriter", throwOnError: true)!;
        var writer = (INotificationWriter)Activator.CreateInstance(type, new OutboxWriter([new Handler()]), new ConfigurationBuilder().Build())!;
        var notification = new NotificationRequest(Guid.CreateVersion7(), "sigma", DateTimeOffset.UtcNow, "Security alert", "A matching event was detected.");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await writer.WriteAsync(notification, connection, transaction, token);
            await transaction.RollbackAsync(token);
        }
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await writer.WriteBatchAsync([notification, notification], connection, transaction, token);
            await transaction.CommitAsync(token);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM scheduling.Outbox";
        var payload = (string?)await command.ExecuteScalarAsync(token);
        Assert.IsNotNull(payload);
        var work = JsonSerializer.Deserialize<TemporalWorkRequest>(payload);
        Assert.IsNotNull(work);
        Assert.AreEqual(NotificationWork.Kind, work.Work.Kind);
        Assert.AreEqual(notification, JsonSerializer.Deserialize<NotificationRequest>(work.Work.Payload));
        command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox";
        Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
    }, context.CancellationToken);

    private sealed class Handler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken token) => throw new NotSupportedException();
    }
}
