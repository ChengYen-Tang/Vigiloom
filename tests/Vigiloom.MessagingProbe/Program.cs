using Microsoft.Data.SqlClient;
using Temporalio.Client;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.MessagingProbe;

public static class Program
{
    public static async Task Main()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL")!;
        var database = Environment.GetEnvironmentVariable("VIGILOOM_PROBE_DATABASE")!;
        var queue = Environment.GetEnvironmentVariable("VIGILOOM_PROBE_QUEUE")!;
        var expected = Guid.Parse(Environment.GetEnvironmentVariable("VIGILOOM_PROBE_MESSAGE")!);
        var client = await TemporalClient.ConnectAsync(new(address));
        var handler = new TransactionalWorkHandler(database, async (work, connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO scheduling.ProbeEffects (MessageId) VALUES (@id)";
            command.Parameters.AddWithValue("@id", Guid.Parse(work.Payload));
            await command.ExecuteNonQueryAsync(token);
            return "received";
        });
        using var worker = ServiceWorkWorker.Create(client, queue, handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await worker.ExecuteAsync(async () =>
        {
            while (true)
            {
                await using var connection = new SqlConnection(database);
                await connection.OpenAsync(timeout.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM scheduling.ProbeEffects WHERE MessageId = @id";
                command.Parameters.AddWithValue("@id", expected);
                if ((int)(await command.ExecuteScalarAsync(timeout.Token))! > 0) return;
                await Task.Delay(200, timeout.Token);
            }
        }, timeout.Token);
    }
}
