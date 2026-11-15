using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostLogDispatchTests(TestContext context)
{
    [TestMethod]
    public Task Stage_UsesCallerTransactionAndStoresOnlyEventIdentities() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "EXEC('CREATE SCHEMA security'); CREATE TABLE security.SigmaRuleActivations (Enabled bit NOT NULL)";
                await schema.ExecuteNonQueryAsync(token);
            }
            var writer = new HostLogDispatchWriter(new OutboxWriter([new Handler()]));
            var dispatch = new HostLogDispatch(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, [Guid.CreateVersion7()]);
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await writer.StageAsync(connection, transaction, dispatch, token);
                await transaction.CommitAsync(token);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox;";
                Assert.AreEqual(0, (int)(await command.ExecuteScalarAsync(token))!);
                command.CommandText = "INSERT INTO security.SigmaRuleActivations (Enabled) VALUES (1)";
                await command.ExecuteNonQueryAsync(token);
            }
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await writer.StageAsync(connection, transaction, dispatch, token);
                await transaction.RollbackAsync(token);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox";
                Assert.AreEqual(0, (int)(await command.ExecuteScalarAsync(token))!);
            }
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await writer.StageAsync(connection, transaction, dispatch, token);
                await transaction.CommitAsync(token);
            }
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT MessageType, Payload FROM scheduling.Outbox";
            await using var row = await read.ExecuteReaderAsync(token);
            Assert.IsTrue(await row.ReadAsync(token));
            Assert.AreEqual(HostLogDispatch.MessageType, row.GetString(0));
            var payload = row.GetString(1);
            using var json = JsonDocument.Parse(payload);
            Assert.AreEqual("AgentId,DeviceGeneration,DeviceId,MessageIds",
                string.Join(',', json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)));
            var stored = JsonSerializer.Deserialize<HostLogDispatch>(payload);
            Assert.IsNotNull(stored);
            Assert.AreEqual(dispatch.AgentId, stored.AgentId);
            Assert.AreEqual(dispatch.DeviceId, stored.DeviceId);
            Assert.AreEqual(dispatch.DeviceGeneration, stored.DeviceGeneration);
            Assert.AreEqual(dispatch.MessageIds[0], Assert.ContainsSingle(stored.MessageIds));
            Assert.IsFalse(await row.ReadAsync(token));
        }, context.CancellationToken);

    private sealed class Handler : IOutboxHandler
    {
        public string MessageType => HostLogDispatch.MessageType;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
