using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Infrastructure.Messaging;
using Wolverine;
using Wolverine.Attributes;
using Microsoft.Data.SqlClient;
using Temporalio.Client;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.MessagingProbe;

public static class Program
{
    public static async Task Main()
    {
        if (Environment.GetEnvironmentVariable("VIGILOOM_PROBE_TRANSPORT") == "temporal")
        {
            await RunTemporalAsync(Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL")!);
            return;
        }
        var database = Environment.GetEnvironmentVariable("VIGILOOM_PROBE_DATABASE")!;
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ")!;
        var queue = Environment.GetEnvironmentVariable("VIGILOOM_PROBE_QUEUE")!;
        var expected = Guid.Parse(Environment.GetEnvironmentVariable("VIGILOOM_PROBE_MESSAGE")!);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<ProbeContext>(x => x.UseSqlServer(database));
        builder.UseWolverine(options =>
        {
            options.UseVigiloomMessaging(database, broker);
            options.Discovery.DisableConventionalDiscovery();
            options.Discovery.IncludeType<ProbeHandler>();
            options.ListenReliablyOn(queue);
        });
        using var host = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await host.StartAsync(timeout.Token);
        try
        {
            while (true)
            {
                await using var scope = host.Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<ProbeContext>();
                if (await context.Receipts.AnyAsync(x => x.Id == expected, timeout.Token)) break;
                await Task.Delay(200, timeout.Token);
            }
        }
        finally { await host.StopAsync(CancellationToken.None); }
    }

    private static async Task RunTemporalAsync(string address)
    {
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

public sealed record ProbeMessage(Guid Id);
public sealed class ProbeReceipt { public Guid Id { get; set; } }
public sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
{
    public DbSet<ProbeReceipt> Receipts => Set<ProbeReceipt>();
}

public sealed class ProbeHandler
{
    [Deduplicated]
    [Transactional]
    public static async Task Handle(ProbeMessage message, ProbeContext context)
    {
        context.Receipts.Add(new ProbeReceipt { Id = message.Id });
        await context.SaveChangesAsync();
    }
}
