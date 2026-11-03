using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class DatabaseHealthSqlTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public Task DatabaseProbe_ReportsFailureAndRecovery() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        // The generated, fixture-owned name is the only target of the OFFLINE/ONLINE commands.
        Assert.MatchesRegex("^Vigiloom_Test_[0-9a-f]{32}_[0-9a-f]{32}$", database.DatabaseName);
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        var checks = factory.Services.GetRequiredService<HealthCheckService>();
        async Task<HealthStatus> ReadAsync(CancellationToken cancellationToken) =>
            (await checks.CheckHealthAsync(check => check.Name == "database", cancellationToken)).Status;
        Assert.AreEqual(HealthStatus.Healthy, await ReadAsync(token));
        var master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        Exception? failure = null;
        try
        {
            command.CommandText = $"ALTER DATABASE [{database.DatabaseName}] SET OFFLINE WITH ROLLBACK IMMEDIATE";
            await command.ExecuteNonQueryAsync(token);
            Assert.AreEqual(HealthStatus.Unhealthy, await ReadAsync(token));
            using var liveness = await client.GetAsync("/health/live", token);
            Assert.AreEqual(System.Net.HttpStatusCode.OK, liveness.StatusCode);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            command.CommandText = $"ALTER DATABASE [{database.DatabaseName}] SET ONLINE";
            try { await command.ExecuteNonQueryAsync(cleanup.Token); }
            catch (Exception cleanupFailure) when (failure is not null)
            { throw new AggregateException("Database health test and restoration both failed.", failure, cleanupFailure); }
        }
        // Invalidate only this test database's stale pool after the offline transition.
        using (var pooled = new SqlConnection(database.ConnectionString)) SqlConnection.ClearPool(pooled);
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
        recovery.CancelAfter(TimeSpan.FromSeconds(20));
        while (await ReadAsync(recovery.Token) != HealthStatus.Healthy)
            await Task.Delay(250, recovery.Token);
    }, testContext.CancellationToken);
}
