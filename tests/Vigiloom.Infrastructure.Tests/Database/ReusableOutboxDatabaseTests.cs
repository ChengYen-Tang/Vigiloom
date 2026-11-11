using Microsoft.Data.SqlClient;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableOutboxDatabaseTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reset_ClearsOutboxAndEffectsInEitherOrder(bool reverse)
    {
        var order = reverse ? new[] { true, false } : new[] { false, true };
        foreach (var effectsFirst in order)
            await ReusableOutboxDatabase.ExecuteAsync(async (database, token) =>
            {
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM scheduling.Outbox) + (SELECT COUNT(*) FROM scheduling.TestEffects)";
                Assert.AreEqual(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT COUNT(*) FROM scheduling.__EFMigrationsHistory";
                Assert.IsGreaterThan(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
                const string outbox = "INSERT INTO scheduling.Outbox (MessageId,MessageType,Payload,CreatedAt,AvailableAt,State,Attempts) VALUES (@id,N'test',N'{}',SYSUTCDATETIME(),SYSUTCDATETIME(),0,0);";
                const string effect = "INSERT INTO scheduling.TestEffects (MessageId) VALUES (@id);";
                command.CommandText = effectsFirst ? effect + outbox : outbox + effect;
                Assert.AreEqual(2, await command.ExecuteNonQueryAsync(token));
            }, context.CancellationToken);
    }
}
