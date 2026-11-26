using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemporalPingTransitionTests(TestContext context)
{
    [TestMethod]
    public Task PersistsDeliveryWithTransactionAndPreservesPredecessor() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
        var sink = new TemporalPingTransitionSink(new OutboxWriter([new Handler()]), new ConfigurationBuilder().Build());
        var transition = new PingTransition(Guid.CreateVersion7(), "example.org", 12, DateTimeOffset.UtcNow,
            Reachability.Reachable, Reachability.Unreachable, new PingDeviceContext("Server", []), 7);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await sink.RecordAsync(transition, connection, transaction, token);
            await transaction.RollbackAsync(token);
        }
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await sink.RecordAsync(transition, connection, transaction, token);
            await transaction.CommitAsync(token);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM scheduling.Outbox";
        var payload = (string?)await command.ExecuteScalarAsync(token);
        Assert.IsNotNull(payload);
        var request = JsonSerializer.Deserialize<TemporalWorkRequest>(payload);
        Assert.IsNotNull(request);
        var delivery = JsonSerializer.Deserialize<PingTransitionDelivery>(request.Work.Payload);
        Assert.IsNotNull(delivery);
        Assert.AreEqual(7L, delivery.PreviousTransitionGeneration);
        Assert.AreEqual(transition.DeviceId, delivery.Transition.DeviceId);
        command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox";
        Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
    }, context.CancellationToken);

    private sealed class Handler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
