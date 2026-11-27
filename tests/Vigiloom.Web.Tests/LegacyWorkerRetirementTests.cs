using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Scheduling;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class LegacyWorkerRetirementTests(TestContext context)
{
    [TestMethod]
    public Task RetiresOnlySupersededTriggersWhenTheirOwnerIsReady() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddVigiloomScheduling(config);
            using var provider = services.BuildServiceProvider();
            var jobs = provider.GetRequiredService<IBackgroundJobClient>();
            var scheduled = jobs.Create(Job.FromExpression(() => Probe(CancellationToken.None)), new ScheduledState(TimeSpan.FromHours(1)));
            var enqueued = jobs.Create(Job.FromExpression(() => Probe(CancellationToken.None)), new EnqueuedState());
            var unrelated = jobs.Create(Job.FromExpression(() => Probe(CancellationToken.None)), new EnqueuedState());
            var payload = new InvocationData("Vigiloom.Worker.Hosting.PingRetentionJob, Vigiloom.Worker", "RunAsync",
                JsonSerializer.Serialize(new[] { typeof(CancellationToken).AssemblyQualifiedName }), "[null]").SerializePayload();
            await using (var sql = new SqlConnection(database.ConnectionString))
            {
                await sql.OpenAsync(token);
                await using var command = sql.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[Job] SET [InvocationData] = @payload WHERE [Id] IN (@scheduled, @enqueued)";
                command.Parameters.AddWithValue("@payload", payload);
                command.Parameters.AddWithValue("@scheduled", long.Parse(scheduled));
                command.Parameters.AddWithValue("@enqueued", long.Parse(enqueued));
                Assert.AreEqual(2, await command.ExecuteNonQueryAsync(token));
            }
            var storage = provider.GetRequiredService<JobStorage>();
            var retirement = new LegacyWorkerRetirement(config, storage, jobs);
            Assert.IsFalse(await retirement.RunAsync(token));
            provider.GetRequiredService<IRecurringJobManager>().AddOrUpdate("vigiloom-ping-retention",
                Job.FromExpression<NetworkPingRetentionJob>(job => job.RunAsync(CancellationToken.None), "network"),
                Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            Assert.IsFalse(await retirement.RunAsync(token));
            config["Services:Network:Url"] = "http://network";
            provider.GetRequiredService<IRecurringJobManager>().AddOrUpdate("test-legacy-worker",
                Job.FromExpression(() => Probe(CancellationToken.None)), Cron.Hourly(), new RecurringJobOptions());
            await using (var sql = new SqlConnection(database.ConnectionString))
            {
                await sql.OpenAsync(token);
                await using var command = sql.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[Hash] SET [Value] = @payload WHERE [Key] = 'recurring-job:test-legacy-worker' AND [Field] = 'Job'";
                command.Parameters.AddWithValue("@payload", payload);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }
            Assert.IsFalse(await retirement.RunAsync(token));
            provider.GetRequiredService<IRecurringJobManager>().RemoveIfExists("test-legacy-worker");
            Assert.IsTrue(await retirement.RunAsync(token));
            using var connection = storage.GetConnection();
            Assert.AreEqual("Deleted", connection.GetStateData(scheduled)?.Name);
            Assert.AreEqual("Deleted", connection.GetStateData(enqueued)?.Name);
            Assert.AreEqual("Enqueued", connection.GetStateData(unrelated)?.Name);
            Assert.AreEqual(1L, storage.GetMonitoringApi().EnqueuedCount("default"));
        }, context.CancellationToken);

    public static Task Probe(CancellationToken token) => Task.CompletedTask;

    [TestMethod]
    public Task StandaloneWebRetiresOldJobsAndSchedulesWithoutWorkerAssembly() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            GlobalConfiguration.Configuration.UseNoOpLogProvider();
            await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
            var dbOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(dbOptions)) await db.Database.MigrateAsync(token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddVigiloomScheduling(config);
            using var provider = services.BuildServiceProvider();
            var jobs = provider.GetRequiredService<IBackgroundJobClient>();
            var legacy = jobs.Create(Job.FromExpression(() => Probe(CancellationToken.None)), new ScheduledState(TimeSpan.FromHours(1)));
            var payload = new InvocationData("Vigiloom.Worker.Hosting.PingRetentionJob, Vigiloom.Worker", "RunAsync",
                JsonSerializer.Serialize(new[] { typeof(CancellationToken).AssemblyQualifiedName }), "[null]").SerializePayload();
            await using (var sql = new SqlConnection(database.ConnectionString))
            {
                await sql.OpenAsync(token);
                await using var command = sql.CreateCommand();
                command.CommandText = "UPDATE [hangfire].[Job] SET [InvocationData] = @payload WHERE [Id] = @id";
                command.Parameters.AddWithValue("@payload", payload);
                command.Parameters.AddWithValue("@id", long.Parse(legacy));
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token));
            }
            provider.GetRequiredService<IRecurringJobManager>().AddOrUpdate("vigiloom-ping-retention",
                Job.FromExpression<NetworkPingRetentionJob>(job => job.RunAsync(CancellationToken.None), "network"),
                Cron.Minutely(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Vigiloom.slnx"))) root = root.Parent;
            Assert.IsNotNull(root);
            var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var webDirectory = Path.Combine(root.FullName, "src", "Hosts", "Vigiloom.Web", "bin", buildConfiguration, "net10.0");
            Assert.IsFalse(File.ReadAllText(Path.Combine(webDirectory, "Vigiloom.Web.deps.json")).Contains("Vigiloom.Worker/", StringComparison.Ordinal));
            Assert.IsFalse(File.Exists(Path.Combine(webDirectory, "Vigiloom.Worker.dll")));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = webDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(Path.Combine(webDirectory, "Vigiloom.Web.dll"));
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["ConnectionStrings__Vigiloom"] = database.ConnectionString;
            start.Environment["ConnectionStrings__Scheduling"] = database.ConnectionString;
            start.Environment["Services__Web__Url"] = $"http://127.0.0.1:{port}";
            start.Environment["Services__Operations__Url"] = "http://127.0.0.1:1";
            start.Environment["Services__Network__Url"] = "http://127.0.0.1:1";
            start.Environment["Services__Resources__Url"] = "http://127.0.0.1:1";
            start.Environment["Services__Observability__Url"] = "http://127.0.0.1:1";
            start.Environment["Vigiloom__SelfTelemetry__Enabled"] = "false";
            start.Environment["Logging__LogLevel__Default"] = "Warning";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Web.");
            var errors = process.StandardError.ReadToEndAsync(token);
            var output = process.StandardOutput.ReadToEndAsync(token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(75));
                using var storage = provider.GetRequiredService<JobStorage>().GetConnection();
                while (storage.GetStateData(legacy)?.Name != "Deleted")
                {
                    if (process.HasExited) Assert.Fail($"Standalone Web exited with code {process.ExitCode}: {await errors} {await output}");
                    await Task.Delay(250, deadline.Token);
                }
                await using (var sql = new SqlConnection(database.ConnectionString))
                {
                    await sql.OpenAsync(deadline.Token);
                    await using var command = sql.CreateCommand();
                    command.CommandText = """
                        UPDATE [hangfire].[Hash] SET [Value] = @past WHERE [Key] = 'recurring-job:vigiloom-ping-retention' AND [Field] = 'LastExecution';
                        IF @@ROWCOUNT = 0 INSERT INTO [hangfire].[Hash] ([Key], [Field], [Value]) VALUES ('recurring-job:vigiloom-ping-retention', 'LastExecution', @past);
                        UPDATE [hangfire].[Set] SET [Score] = 0 WHERE [Key] = 'recurring-jobs' AND [Value] = 'vigiloom-ping-retention';
                        """;
                    command.Parameters.AddWithValue("@past", DateTime.UtcNow.AddMinutes(-2).ToString("O"));
                    await command.ExecuteNonQueryAsync(deadline.Token);
                }
                var monitor = provider.GetRequiredService<JobStorage>().GetMonitoringApi();
                while (monitor.EnqueuedCount("network") == 0)
                {
                    if (process.HasExited) Assert.Fail($"Standalone Web exited with code {process.ExitCode}: {await errors} {await output}");
                    await Task.Delay(250, deadline.Token);
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
        }, context.CancellationToken);
}
