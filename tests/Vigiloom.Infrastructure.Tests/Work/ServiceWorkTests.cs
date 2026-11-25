using Temporalio.Client;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Infrastructure.Tests.Work;

[TestClass]
[TestCategory("Integration")]
public sealed class ServiceWorkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ProducerSubmitsWorkToTheDestinationWorkerAndReadsItsResult()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL to run the Temporal integration test.");

        var client = await TemporalClient.ConnectAsync(new(address));
        var queue = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var id = "vigiloom-test-" + Guid.CreateVersion7().ToString("N");
        var handler = new TestHandler();
        using var worker = ServiceWorkWorker.Create(client, queue, handler);
        await worker.ExecuteAsync(async () =>
        {
            var producer = new ServiceWorkClient(client);
            await producer.StartAsync(id, queue, new("inventory", "device-123"));
            var result = await producer.Get(id).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
            Assert.AreEqual("inventory:device-123", result);
            Assert.AreEqual(1, handler.Executions);
        }, TestContext.CancellationToken);
    }

    private sealed class TestHandler : IServiceWorkHandler
    {
        public int Executions { get; private set; }
        public Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult<string?>($"{work.Kind}:{work.Payload}");
        }
    }
}
