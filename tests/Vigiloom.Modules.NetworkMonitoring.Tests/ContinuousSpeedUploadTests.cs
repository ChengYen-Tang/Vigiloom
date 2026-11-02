using System.Net;
using NetPace.Core.Clients.Ookla;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class ContinuousSpeedUploadTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task CompletedLaneStartsAnotherRequestWhileOtherLanesAreStillBusy()
    {
        var handler = new OverlapHandler();
        using var client = new HttpClient(handler);
        var budget = new SpeedTrafficBudget(20 * 1024 * 1024, () => { }, TestContext.CancellationToken);
        var server = new OoklaServer { Id = 1, Url = "http://speedtest.example.com:8080/speedtest/upload.php" };
        var pending = ContinuousSpeedUpload.RunAsync(client, server, budget, 10, 1000, 5, 4,
            _ => { }, TestContext.CancellationToken);
        await handler.NextRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
        var result = await pending;
        Assert.AreEqual(1, handler.Probes);
        Assert.IsGreaterThanOrEqualTo(5, handler.Requests);
        Assert.AreEqual(0, result.Failures);
    }

    private sealed class OverlapHandler : HttpMessageHandler
    {
        private int requests, probes;
        private readonly TaskCompletionSource firstFour = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests => Volatile.Read(ref requests);
        public int Probes => Volatile.Read(ref probes);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Content?.Headers.ContentLength == 0)
                Interlocked.Increment(ref probes);
            else
            {
                var number = Interlocked.Increment(ref requests);
                if (number == 4) firstFour.TrySetResult();
                if (number <= 4) await firstFour.Task.WaitAsync(token);
                if (number == 5) NextRequestStarted.TrySetResult();
                if (number is >= 2 and <= 4) await NextRequestStarted.Task.WaitAsync(token);
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([]) };
        }
    }
}
