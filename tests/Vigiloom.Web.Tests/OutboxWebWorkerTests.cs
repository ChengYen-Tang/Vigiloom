using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class OutboxWebWorkerTests(TestContext context)
{
    [TestMethod]
    public Task WebProcessesPlatformOutboxWithoutTouchingOperationsOutbox() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (platform, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operations, token) =>
        {
            await HangfireSchemaInstaller.InstallAsync(platform.ConnectionString, token);
            foreach (var connectionString in new[] { platform.ConnectionString, operations.ConnectionString })
            {
                var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(connectionString,
                    sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
                await using var db = new SchedulingDbContext(options);
                await db.Database.MigrateAsync(token);
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await new OutboxWriter([new Handler()]).WriteAsync(new OutboxMessage(Guid.CreateVersion7(), Handler.Type, "{}"), db, token);
                await transaction.CommitAsync(token);
            }
            using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseSetting("Services:Operations:Url", "http://operations");
                host.UseSetting("Services:Web:Url", "http://web");
                host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Vigiloom"] = platform.ConnectionString, ["ConnectionStrings:Scheduling"] = platform.ConnectionString }));
                host.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOutboxHandler>();
                    services.AddSingleton<IOutboxHandler, Handler>();
                });
            });
            using var client = web.CreateClient();
            web.Services.GetRequiredService<IBackgroundJobClient>().Enqueue<OutboxDispatcher>(dispatcher => dispatcher.DispatchAsync(CancellationToken.None));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while (await StateAsync(platform.ConnectionString, deadline.Token) != 2)
                await Task.Delay(200, deadline.Token);
            Assert.AreEqual(0, await StateAsync(operations.ConnectionString, token));
        }, outerToken), context.CancellationToken);

    private static async Task<int> StateAsync(string connectionString, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [State] FROM [scheduling].[Outbox]";
        return (int)(await command.ExecuteScalarAsync(token))!;
    }

    private sealed class Handler : IOutboxHandler
    {
        public const string Type = "test.web.outbox";
        public string MessageType => Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
