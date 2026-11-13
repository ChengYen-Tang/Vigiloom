using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableWebDatabaseTests(TestContext context)
{
    [TestMethod]
    public async Task FailedCase_ClearsInitialAdministratorBeforeNextHost()
    {
        const string setupToken = "test-only-bootstrap-secret-with-adequate-length";
        string? firstDatabase = null;
        async Task CreateAdministrator(Vigiloom.Testing.SqlServer.SqlTestDatabase database, CancellationToken token)
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Vigiloom:SetupToken"] = setupToken })));
            var identities = host.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await RoleInitializer.InitializeAsync(identities, token);
            await using var scope = host.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                .CreateAsync(setupToken, "admin", "Strong-Password-123", token);
            Assert.AreEqual(InitialAdministratorStatus.Created, result.Status);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            firstDatabase = database.DatabaseName;
            await CreateAdministrator(database, token);
            throw new InvalidOperationException("Expected test failure");
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            Assert.AreEqual(firstDatabase, database.DatabaseName);
            await CreateAdministrator(database, token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task FailedCase_IsResetBeforeAnotherHost()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            await using var db = Open(database.ConnectionString);
            db.Devices.Add(new Device("failed", "127.0.0.1", false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
            throw new InvalidOperationException("Expected failure");
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            await using var db = Open(database.ConnectionString);
            Assert.AreEqual(0, await db.Devices.CountAsync(token));
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
            using var client = PingTestSite.Client(host);
            await PingTestSite.LoginAsync(client, "reader", token);
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task ServiceAndQuotaData_AreResetBetweenCases()
    {
        for (var pass = 0; pass < 2; pass++)
            await ReusableWebDatabase.ExecuteAsync(async (database, token) =>
            {
                var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
                await using (var db = new ServiceObservabilityDbContext(options))
                {
                    Assert.AreEqual(0, await db.Sources.CountAsync(token));
                    db.Sources.Add(new ServiceSource("source", new byte[32], DateTimeOffset.UtcNow));
                    await db.SaveChangesAsync(token);
                }
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM [logquota].[Policies]";
                Assert.AreEqual(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "INSERT INTO [logquota].[Policies] ([Id],[Name],[SourceRate],[SourceBurst],[GlobalRate],[GlobalBurst],[CreatedAt],[UpdatedAt]) VALUES (@id,N'central',1,100,1,100,SYSUTCDATETIME(),SYSUTCDATETIME())";
                command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }, context.CancellationToken);
    }

    [TestMethod]
    public async Task ChangedServiceSeed_IsRejectedAndDatabaseIsNotReused()
    {
        string? prior = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            prior = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [service].[ReceptionRejections] SET [ObservedCount] = [ObservedCount] + 1 WHERE [Kind] = 1";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }

    [TestMethod]
    public async Task RemovedTailSeed_IsRejectedAndDatabaseIsNotReused()
    {
        string? prior = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            prior = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM [operations].[WorkerDiagnosticSlots] WHERE [Slot] = 64";
            await command.ExecuteNonQueryAsync(token);
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(64)]
    public async Task ChangedSeed_IsRejectedAndDatabaseIsNotReused(int slot)
    {
        string? prior = null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            prior = database.DatabaseName;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [operations].[WorkerDiagnosticSlots] SET [PingFailures] = [PingFailures] + 1 WHERE [Slot] = @slot";
            command.Parameters.AddWithValue("@slot", slot);
            await command.ExecuteNonQueryAsync(token);
        }, context.CancellationToken));
        await ReusableWebDatabase.ExecuteAsync((database, _) =>
        {
            Assert.AreNotEqual(prior, database.DatabaseName);
            return Task.CompletedTask;
        }, context.CancellationToken);
    }

    private static ResourcesDbContext Open(string connection) => new(new DbContextOptionsBuilder<ResourcesDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options);
}
