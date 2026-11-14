using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Tests.Fixtures;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusablePackageDatabaseTests(TestContext context)
{
    [TestMethod]
    public async Task Reset_ClearsAuditDataAndPreservesSeeds()
    {
        string? name = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await ReusablePackageDatabase.ExecuteAsync(async (database, token) =>
            {
                name ??= database.DatabaseName;
                Assert.AreEqual(name, database.DatabaseName);
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM [operations].[AuditEvents]";
                Assert.AreEqual(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "INSERT INTO [operations].[AuditEvents] ([ActorId], [ResourceId], [OccurredAt], [Kind], [Details]) VALUES (@actor, @resource, @time, 1, N'{}')";
                command.Parameters.AddWithValue("@actor", Guid.CreateVersion7());
                command.Parameters.AddWithValue("@resource", Guid.CreateVersion7());
                command.Parameters.AddWithValue("@time", DateTimeOffset.UtcNow);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
                command.CommandText = "SELECT COUNT(*) FROM [operations].[WorkerDiagnosticSlots]";
                Assert.AreEqual(64, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                var agentOptions = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(database.ConnectionString).Options;
                await using var agents = new AgentManagementDbContext(agentOptions);
                Assert.AreEqual(0, await agents.PackageSources.CountAsync(token));
                agents.PackageSources.Add(new AgentPackageSource { Id = Guid.CreateVersion7(), BaseUri = "https://example.test/" });
                await agents.SaveChangesAsync(token);
                var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString).Options;
                await using var resources = new ResourcesDbContext(options);
                Assert.AreEqual(0, await resources.Devices.CountAsync(token));
                resources.Devices.Add(new Device("temporary", "127.0.0.1", true, DateTimeOffset.UtcNow));
                await resources.SaveChangesAsync(token);
            }, context.CancellationToken);
        }
    }

    [TestMethod]
    public async Task ChangedPreservedSeed_DiscardsDatabase()
    {
        string? name = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusablePackageDatabase.ExecuteAsync(async (database, token) =>
        {
            name = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [operations].[WorkerDiagnosticSlots] SET [PingFailures] = [PingFailures] + 1 WHERE [Slot] = 64";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
        }, context.CancellationToken));
        await ReusablePackageDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(name, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }
}
