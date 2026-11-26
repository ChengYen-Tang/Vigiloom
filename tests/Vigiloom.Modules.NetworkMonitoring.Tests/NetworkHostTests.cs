extern alias NetworkHost;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NetworkHostTests(TestContext context)
{
    [TestMethod]
    public Task IndependentHostRunsPingAndQueuesTransition() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var networkOptions = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new NetworkDbContext(networkOptions)) await db.Database.MigrateAsync(token);
        await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
        var id = Guid.CreateVersion7();
        var version = new byte[8];
        await using (var db = new NetworkDbContext(networkOptions))
        {
            db.PingTargets.Add(new PingTargetState { DeviceId = id, Target = "example.org", DeviceVersion = version,
                Enabled = true, Successes = 1, DueAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync(token);
        }
        using var host = new WebApplicationFactory<NetworkHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hangfire:Enabled", "false");
            builder.UseSetting("Temporal:EnableNetworkWorker", "false");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Network"] = database.ConnectionString, ["Hangfire:Enabled"] = "false" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDevicePingSource>();
                services.AddSingleton<IDevicePingSource>(new Source(id, version));
                services.RemoveAll<IPingProbe>();
                services.AddSingleton<IPingProbe, Probe>();
                services.RemoveAll<IOutboxHandler>();
                services.AddSingleton<IOutboxHandler, Handler>();
                foreach (var item in services.Where(item => item.ImplementationType?.Name is "WorkDispatcher" or "SpeedWorker").ToArray()) services.Remove(item);
            });
        });
        using var client = host.CreateClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var db = new NetworkDbContext(networkOptions);
            if (await db.PingSamples.AnyAsync(deadline.Token)) break;
            await Task.Delay(100, deadline.Token);
        }
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scheduling.Outbox";
        Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync(token))!);
    }, context.CancellationToken);

    private sealed class Source(Guid id, byte[] version) : IDevicePingSource
    {
        public async IAsyncEnumerable<PingDeviceSnapshot> ReadEnabledAsync([EnumeratorCancellation] CancellationToken token)
        { yield return new(id, version, ["example.org"]); await Task.CompletedTask; }
        public Task<bool> IsCurrentAsync(Guid id, byte[] version, string target, DbConnection connection, DbTransaction transaction, CancellationToken token) => Task.FromResult(true);
        public Task<PingDeviceContext> GetContextAsync(Guid id, DbConnection connection, DbTransaction transaction, CancellationToken token) => Task.FromResult(new PingDeviceContext("Example", []));
    }
    private sealed class Probe : IPingProbe
    {
        public Task<PingMeasurement> MeasureAsync(string target, int timeoutSeconds, CancellationToken token) => Task.FromResult(new PingMeasurement(PingOutcome.Success, target, 1));
    }
    private sealed class Handler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
