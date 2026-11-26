extern alias NetworkHost;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NetworkInstancesTests(TestContext context)
{
    [TestMethod]
    public Task TwoNetworkHostsShareDueTargets() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var options = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new NetworkDbContext(options)) await db.Database.MigrateAsync(token);
        await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
        var version = new byte[8];
        var devices = new[] { new PingDeviceSnapshot(Guid.CreateVersion7(), version, ["first.example"]),
            new PingDeviceSnapshot(Guid.CreateVersion7(), version, ["second.example"]) };
        await using (var db = new NetworkDbContext(options))
        {
            db.PingSettings.Add(new PingSetting { MaxConcurrency = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            foreach (var device in devices)
                db.PingTargets.Add(new PingTargetState { DeviceId = device.Id, Target = device.Targets[0], DeviceVersion = version,
                    Enabled = true, DueAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync(token);
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProbe = new Probe(release.Task);
        var secondProbe = new Probe(release.Task);
        WebApplicationFactory<NetworkHost::Program> Create(Probe probe) => new WebApplicationFactory<NetworkHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hangfire:Enabled", "false");
            builder.UseSetting("Temporal:EnableNetworkWorker", "false");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Network"] = database.ConnectionString, ["Hangfire:Enabled"] = "false", ["Temporal:EnableNetworkWorker"] = "false" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDevicePingSource>();
                services.AddSingleton<IDevicePingSource>(new Source(devices));
                services.RemoveAll<IPingProbe>();
                services.AddSingleton<IPingProbe>(probe);
                foreach (var item in services.Where(item => item.ImplementationType?.Name is "WorkDispatcher" or "SpeedWorker").ToArray()) services.Remove(item);
            });
        });
        try
        {
            using var first = Create(firstProbe);
            using var firstClient = first.CreateClient();
            var firstTarget = await firstProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            using var second = Create(secondProbe);
            using var secondClient = second.CreateClient();
            var secondTarget = await secondProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.AreNotEqual(firstTarget, secondTarget);
            release.TrySetResult();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                await using var db = new NetworkDbContext(options);
                if (await db.PingSamples.CountAsync(deadline.Token) == 2) break;
                await Task.Delay(100, deadline.Token);
            }
        }
        finally { release.TrySetResult(); }
    }, context.CancellationToken);

    private sealed class Source(PingDeviceSnapshot[] devices) : IDevicePingSource
    {
        public async IAsyncEnumerable<PingDeviceSnapshot> ReadEnabledAsync([EnumeratorCancellation] CancellationToken token)
        { foreach (var device in devices) yield return device; await Task.CompletedTask; }
        public Task<bool> IsCurrentAsync(Guid id, byte[] version, string target, DbConnection connection, DbTransaction transaction, CancellationToken token) => Task.FromResult(true);
        public Task<PingDeviceContext> GetContextAsync(Guid id, DbConnection connection, DbTransaction transaction, CancellationToken token) => Task.FromResult(new PingDeviceContext("Device", []));
    }
    private sealed class Probe(Task release) : IPingProbe
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PingMeasurement> MeasureAsync(string target, int timeoutSeconds, CancellationToken token)
        {
            Started.TrySetResult(target);
            await release.WaitAsync(token);
            return new(PingOutcome.Success, target, 1);
        }
    }
}
