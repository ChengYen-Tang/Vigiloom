using Hangfire;
using Hangfire.SqlServer;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Worker.Hosting;

namespace Vigiloom.Worker.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LineScheduleCutoverTests(TestContext context)
{
    [TestMethod]
    public Task RemovesLegacyScheduleAndSkipsAlreadyQueuedJob() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var storage = new SqlServerStorage(database.ConnectionString, SchedulingRegistration.StorageOptions(prepareSchema: true));
            using (storage.GetConnection()) { }
            var manager = new RecurringJobManager(storage);
            var scheduleType = typeof(WorkerHost).Assembly.GetType("Vigiloom.Worker.Hosting.LineDeliverySchedule", throwOnError: true)!;
            async Task StartAsync(string? operations)
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:Operations:Url"] = operations }).Build();
                using var services = new ServiceCollection().AddSingleton<IRecurringJobManager>(manager).AddSingleton<IConfiguration>(config).BuildServiceProvider();
                var schedule = (IHostedService)ActivatorUtilities.CreateInstance(services, scheduleType);
                await schedule.StartAsync(token);
            }
            await StartAsync(null);
            using (var connection = storage.GetConnection()) Assert.IsTrue(connection.GetRecurringJobs().Any(job => job.Id == "vigiloom-line-delivery"));
            await StartAsync("http://operations");
            using (var connection = storage.GetConnection()) Assert.IsFalse(connection.GetRecurringJobs().Any(job => job.Id == "vigiloom-line-delivery"));

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:Operations:Url"] = "http://operations" }).Build();
            var jobType = typeof(WorkerHost).Assembly.GetType("Vigiloom.Worker.Hosting.LineDeliveryJob", throwOnError: true)!;
            var jobInstance = Activator.CreateInstance(jobType, [null, config])!;
            await (Task)jobType.GetMethod("RunAsync")!.Invoke(jobInstance, [token])!;
            var handlerType = typeof(WorkerHost).Assembly.GetType("Vigiloom.Worker.Hosting.LegacyLineDeliveryHandler", throwOnError: true)!;
            var handler = (IOutboxHandler)Activator.CreateInstance(handlerType, [null, config])!;
            await handler.HandleAsync(new(Guid.CreateVersion7(), handler.MessageType, "{}"), null!, null!, token);
        }, context.CancellationToken);
}
