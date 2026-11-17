using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Hosting;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentEnrollmentWorkerTests(TestContext context)
{
    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task HttpTimeoutIsRetriedWithoutStoppingAgent()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"enrollment-{Guid.NewGuid():N}");
        try
        {
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
            {
                var path = Path.Combine(root, AgentEnrollmentBootstrap.FileName);
                await File.WriteAllTextAsync(path, $"vle_{Guid.CreateVersion7():D}.{new string('a', 43)}", context.CancellationToken);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var id = Guid.CreateVersion7();
            var handler = new TimeoutThenSuccessHandler(id);
            using var http = new HttpClient(handler);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            }).Build();
            var clock = new RetryClock();
            using var worker = new AgentEnrollmentWorker(new AgentEnrollmentBootstrap(new IdentityClient(http)), configuration,
                clock, NullLogger<AgentEnrollmentWorker>.Instance);
            await worker.StartAsync(context.CancellationToken);
            try
            {
                await clock.Scheduled.Task.WaitAsync(context.CancellationToken);
                clock.Advance();
                await worker.ExecuteTask!.WaitAsync(context.CancellationToken);
                await using var session = vault.Acquire();
                Assert.AreEqual(id, (await session.ReadAsync(context.CancellationToken))!.AgentId);
                Assert.IsFalse(File.Exists(Path.Combine(root, AgentEnrollmentBootstrap.FileName)));
                Assert.AreEqual(2, handler.Requests);
            }
            finally { await worker.StopAsync(context.CancellationToken); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class TimeoutThenSuccessHandler(Guid agentId) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (++Requests == 1) throw new TaskCanceledException("Simulated HTTP timeout.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { AgentId = agentId }) });
        }
    }

    private sealed class RetryClock : TimeProvider
    {
        private readonly FakeTimeProvider inner = new();
        public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Advance() => inner.Advance(TimeSpan.FromSeconds(30));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Scheduled.TrySetResult();
            return timer;
        }
    }
}
