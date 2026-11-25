using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Client;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Exceptions;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Work;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class CrossProcessTemporalTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CommittedWorkReachesSeparateServiceAfterItStarts()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");
        var temporal = await TemporalClient.ConnectAsync(new(address));
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var id = Guid.CreateVersion7();
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (destination, token) =>
        {
            await MigrateAsync(source.ConnectionString, token);
            await MigrateAsync(destination.ConnectionString, token);
            await using (var db = new SqlConnection(destination.ConnectionString))
            {
                await db.OpenAsync(token);
                await using var setup = db.CreateCommand();
                setup.CommandText = "CREATE TABLE scheduling.ProbeEffects (MessageId uniqueidentifier NOT NULL PRIMARY KEY)";
                await setup.ExecuteNonQueryAsync(token);
            }
            var handler = new TemporalOutboxHandler(new ServiceWorkClient(temporal));
            await using (var db = new SqlConnection(source.ConnectionString))
            {
                await db.OpenAsync(token);
                await using var transaction = (SqlTransaction)await db.BeginTransactionAsync(token);
                var request = new TemporalWorkRequest(queue, new("probe", id.ToString("D")));
                await new OutboxWriter([handler]).WriteAsync(new(id, handler.MessageType, JsonSerializer.Serialize(request)), db, transaction, token);
                await transaction.CommitAsync(token);
            }
            try
            {
                await new OutboxDispatcher(source.ConnectionString, [handler], NullLogger<OutboxDispatcher>.Instance).DispatchAsync(token);

                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Vigiloom.MessagingProbe/bin", configuration, "net10.0/Vigiloom.MessagingProbe.dll"));
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(probe);
                start.Environment["VIGILOOM_TEST_TEMPORAL"] = address;
                start.Environment["VIGILOOM_PROBE_TRANSPORT"] = "temporal";
                start.Environment["VIGILOOM_PROBE_DATABASE"] = destination.ConnectionString;
                start.Environment["VIGILOOM_PROBE_QUEUE"] = queue;
                start.Environment["VIGILOOM_PROBE_MESSAGE"] = id.ToString("D");
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Temporal probe.");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(75));
                    await child.WaitForExitAsync(timeout.Token);
                    Assert.AreEqual(0, child.ExitCode);
                    await using var db = new SqlConnection(destination.ConnectionString);
                    await db.OpenAsync(timeout.Token);
                    await using var verify = db.CreateCommand();
                    verify.CommandText = "SELECT COUNT(*) FROM scheduling.ProbeEffects WHERE MessageId = @id";
                    verify.Parameters.AddWithValue("@id", id);
                    Assert.AreEqual(1, (int)(await verify.ExecuteScalarAsync(timeout.Token))!);
                }
                finally
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await child.WaitForExitAsync(cleanup.Token);
                    }
                }
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await temporal.WorkflowService.DeleteWorkflowExecutionAsync(new DeleteWorkflowExecutionRequest
                    {
                        Namespace = "default",
                        WorkflowExecution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = id.ToString("D") }
                    }, new RpcOptions { CancellationToken = cleanup.Token });
                }
                catch (RpcException error) when (error.Code == RpcException.StatusCode.NotFound) { }
            }
        }, outerToken), TestContext.CancellationToken);
    }

    private static async Task MigrateAsync(string connectionString, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(connectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using var db = new SchedulingDbContext(options);
        await db.Database.MigrateAsync(token);
    }
}
