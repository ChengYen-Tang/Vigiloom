using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InternalApiTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task ReturnsEnabledTargetsAndCurrentAgentEligibility() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Resources"] = database.ConnectionString }));
            });
            var contexts = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var now = DateTimeOffset.UtcNow;
            var active = new Device("Active", "active.example", true, now);
            var disabled = new Device("Disabled", "disabled.example", true, now);
            disabled.Update("Disabled", "disabled.example", true, false, now);
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                db.Devices.AddRange(active, disabled);
                db.DeviceTargets.Add(new DeviceTarget(active.Id, "extra.example"));
                await db.SaveChangesAsync(token);
            }

            using var client = host.CreateClient();
            var targets = await client.GetFromJsonAsync<PingDeviceSnapshot[]>("/internal/v1/ping-targets", token);
            Assert.IsNotNull(targets);
            Assert.HasCount(1, targets);
            Assert.AreEqual(active.Id, targets[0].Id);
            CollectionAssert.AreEquivalent(new[] { "active.example", "extra.example" }, targets[0].Targets.ToArray());
            var activeAgent = await client.GetFromJsonAsync<DeviceAgentState>($"/internal/v1/devices/{active.Id:D}/agent", token);
            var disabledAgent = await client.GetFromJsonAsync<DeviceAgentState>($"/internal/v1/devices/{disabled.Id:D}/agent", token);
            Assert.IsTrue(activeAgent?.Eligible);
            Assert.IsFalse(disabledAgent?.Eligible);
            using var missing = await client.GetAsync($"/internal/v1/devices/{Guid.CreateVersion7():D}/agent", token);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }, TestContext.CancellationToken);

    [AssemblyCleanup]
    public static void Cleanup() => SqlTestDatabase.CompleteRun();
}
