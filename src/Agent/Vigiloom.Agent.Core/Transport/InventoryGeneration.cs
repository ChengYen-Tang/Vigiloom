using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Transport;

public sealed partial class DurableBatchQueue
{
    /// <summary>Reserves above the highest generation returned by the authenticated central platform.</summary>
    public async Task<long> ReserveInventoryGenerationAsync(AgentSettingsOwner owner, InventorySource source, long centralHighWater, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (centralHighWater is < 0 or long.MaxValue) throw new ArgumentOutOfRangeException(nameof(centralHighWater));
        await gate.WaitAsync(token);
        try
        {
            await using var session = await vault.AcquireForTransportAsync(token);
            if (!await IsOwnerAsync(session, owner, token)) throw new InvalidOperationException("Agent identity changed.");
            using var connection = Open(session.DirectoryPath);
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE IF NOT EXISTS inventory_generations (source INTEGER PRIMARY KEY, generation INTEGER NOT NULL CHECK (typeof(generation) = 'integer' AND generation > 0))";
                schema.ExecuteNonQuery();
            }
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO inventory_generations(source, generation) VALUES ($source, $floor + 1)
                ON CONFLICT(source) DO UPDATE SET generation = MAX(generation, $floor) + 1
                WHERE typeof(generation) = 'integer' AND generation > 0 AND generation < 9223372036854775807
                RETURNING generation
                """;
            command.Parameters.AddWithValue("$source", (int)source);
            command.Parameters.AddWithValue("$floor", centralHighWater);
            var value = command.ExecuteScalar();
            if (value is not long generation) throw new InvalidOperationException("Inventory generation is exhausted.");
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return generation;
        }
        finally { gate.Release(); }
    }
}
