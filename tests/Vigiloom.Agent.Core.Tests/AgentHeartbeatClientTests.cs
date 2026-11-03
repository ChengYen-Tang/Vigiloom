using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentHeartbeatClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Heartbeat_DoesNotReportAnotherIdentitysAppliedSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var actualId = Guid.CreateVersion7();
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = actualId, MachineSecret = secret }, token);
            var handler = new CaptureHandler();
            using var http = new HttpClient(handler);
            var snapshot = AgentSettingsSnapshot.Create(AgentSettings.Default);
            var report = new AgentConfigurationReport(snapshot, DateTimeOffset.UtcNow, null);
            var otherOwner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            Assert.AreEqual(HeartbeatResult.Accepted, await new AgentHeartbeatClient(http).SendAsync(vault, new Uri("https://example.test/"), new(otherOwner, report), token));
            Assert.IsNull(handler.Reports.Single().Configuration);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Heartbeat_OlderPlatformReceivesCompatibleQueueStatus(bool rejectConfiguration)
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, token);
            var handler = new OlderConfigurationHandler(rejectConfiguration);
            using var http = new HttpClient(handler);
            var snapshot = AgentSettingsSnapshot.Create(AgentSettings.Default);
            var report = new AgentConfigurationReport(snapshot, DateTimeOffset.UtcNow, null);
            var observation = new AgentConfigurationObservation(new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory), report);
            Assert.AreEqual(HeartbeatResult.Accepted, await new AgentHeartbeatClient(http).SendAsync(vault, new Uri("https://example.test/"), observation, token));
            Assert.HasCount(rejectConfiguration ? 3 : 2, handler.Reports);
            Assert.IsNotNull(handler.Reports[0].Configuration);
            Assert.IsNotNull(handler.Reports[0].Quarantine);
            Assert.IsNotNull(handler.Reports[1].Configuration);
            Assert.IsNull(handler.Reports[1].Quarantine);
            if (rejectConfiguration) Assert.IsNull(handler.Reports[2].Configuration);
            Assert.AreEqual(handler.Reports[0].Sequence, handler.Reports[1].Sequence);
            Assert.AreEqual(handler.Reports[0].Queue, handler.Reports[1].Queue);
            Assert.IsTrue(handler.Reports.All(x => x.Sequence == handler.Reports[0].Sequence && x.Queue == handler.Reports[0].Queue));
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Heartbeat_FallsBackToEmptyBodyForOlderPlatform()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, token);
            var handler = new LegacyHandler();
            using var http = new HttpClient(handler);
            Assert.AreEqual(HeartbeatResult.Accepted, await new AgentHeartbeatClient(http).SendAsync(vault, new Uri("https://example.test/"), token));
            Assert.AreEqual(2, handler.Count);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Heartbeat_ReportsQueuePressureAndContinuesWhenQueueCannotBeRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, token);
            var queue = new DurableBatchQueue(vault, id);
            var batchId = Guid.CreateVersion7();
            await queue.EnqueueAsync(batchId, DateTimeOffset.UtcNow, 1, new byte[] { 1, 2, 3 }, token);
            await queue.QuarantineAsync(batchId, QueueRejection.Conflict, token);
            var expiredId = Guid.CreateVersion7();
            await queue.EnqueueAsync(expiredId, DateTimeOffset.UtcNow, 1, new byte[] { 4, 5, 6 }, token);
            await queue.QuarantineAsync(expiredId, QueueRejection.Expired, token);
            var unknownId = Guid.CreateVersion7();
            await queue.EnqueueAsync(unknownId, DateTimeOffset.UtcNow, 1, new byte[] { 7, 8, 9 }, token);
            await queue.QuarantineAsync(unknownId, QueueRejection.BadRequest, token);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE batches SET rejection = 99 WHERE batch_id = $id";
                command.Parameters.AddWithValue("$id", unknownId.ToString("D"));
                Assert.AreEqual(1, command.ExecuteNonQuery());
            }
            var handler = new CaptureHandler();
            using var http = new HttpClient(handler);
            var client = new AgentHeartbeatClient(http);
            Assert.AreEqual(HeartbeatResult.Accepted, await client.SendAsync(vault, new Uri("https://example.test/"), token));
            var report = handler.Reports[0];
            Assert.AreEqual(1, report.Version);
            Assert.AreEqual(new AgentQueueSnapshot(9, 3, 3, DurableBatchQueue.DefaultCapacityBytes, DurableBatchQueue.MaximumQueuedBatches), report.Queue);
            Assert.AreEqual(new AgentQuarantineBreakdown(0, 0, 1, 0, 0, 1, 1), report.Quarantine);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version = 3"; command.ExecuteNonQuery();
            }
            Assert.AreEqual(HeartbeatResult.Accepted, await client.SendAsync(vault, new Uri("https://example.test/"), token));
            Assert.HasCount(2, handler.Reports);
            Assert.IsNull(handler.Reports[1].Queue);
            Assert.IsNull(handler.Reports[1].Quarantine);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Heartbeat_RetriesTheSameSessionAfterLostReplyAndDoesNotReclaimAReplacedSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, token);
            var handler = new SessionHandler();
            using var http = new HttpClient(handler);
            var client = new AgentHeartbeatClient(http);
            var server = new Uri("https://example.test/");
            Assert.AreEqual(HeartbeatResult.Accepted, await client.SendAsync(vault, server, token), "A lost session response must not suppress the heartbeat.");
            Assert.AreEqual(HeartbeatResult.Accepted, await client.SendAsync(vault, server, token));
            Assert.AreEqual(1, handler.Reads);
            Assert.HasCount(2, handler.Requests);
            Assert.AreEqual(handler.Requests[0], handler.Requests[1], "A lost response must retry the same CAS request.");
            Assert.AreEqual(HeartbeatResult.SessionReplaced, await client.SendAsync(vault, server, token));
            Assert.AreEqual(HeartbeatResult.SessionReplaced, await client.SendAsync(vault, server, token));
            Assert.AreEqual(1, handler.Reads, "A replaced client must not acquire another session.");
            Assert.HasCount(2, handler.Requests);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("oversized")]
    [DataRow("stalled")]
    [DataRow("invalid")]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Heartbeat_ContinuesAndRetriesWhenSessionResponseFails(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = Guid.CreateVersion7(), MachineSecret = secret }, token);
            var handler = new FaultyDiscoveryHandler(failure);
            using var http = new HttpClient(handler);
            var client = new AgentHeartbeatClient(http);
            var server = new Uri("https://example.test/");
            Assert.AreEqual(HeartbeatResult.Accepted, await client.SendAsync(vault, server, token));
            Assert.AreEqual(HeartbeatResult.Accepted, await client.SendAsync(vault, server, token));
            Assert.AreEqual(2, handler.Heartbeats);
            Assert.AreEqual(1, handler.Reports);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Heartbeat_CallerCancellationReleasesGateForNextAttempt()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = Guid.CreateVersion7(), MachineSecret = secret }, token);
            var handler = new FaultyDiscoveryHandler("stalled");
            using var http = new HttpClient(handler);
            var client = new AgentHeartbeatClient(http);
            var server = new Uri("https://example.test/");
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancelled.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(vault, server, cancelled.Token));
            Assert.AreEqual(HeartbeatResult.Accepted, await client.SendAsync(vault, server, token));
            Assert.AreEqual(1, handler.Reports);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FaultyDiscoveryHandler(string failure) : HttpMessageHandler
    {
        public int Heartbeats { get; private set; }
        public int Reports { get; private set; }
        private int discoveries;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/queue-session")
            {
                if (request.Method == HttpMethod.Post) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                if (++discoveries > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentQueueSession(null)) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = failure switch
                    {
                        "stalled" => new StalledContent(),
                        "oversized" => new ByteArrayContent(new byte[1025]),
                        _ => new StringContent("invalid json")
                    }
                });
            }
            if (request.Content is not null) Reports++;
            Heartbeats++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.InfiniteTimeSpan);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class OlderConfigurationHandler(bool rejectConfiguration) : HttpMessageHandler
    {
        public List<AgentHeartbeatReport> Reports { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/queue-session")
                return request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentQueueSession(null)) }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            Reports.Add((await request.Content!.ReadFromJsonAsync<AgentHeartbeatReport>(cancellationToken: token))!);
            var report = Reports[^1];
            return new HttpResponseMessage(report.Quarantine is not null || rejectConfiguration && report.Configuration is not null
                ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent);
        }
    }

    private sealed class SessionHandler : HttpMessageHandler
    {
        public int Reads { get; private set; }
        public List<AgentQueueSessionRequest> Requests { get; } = [];
        private int reports;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/queue-session")
            {
                if (request.Method == HttpMethod.Get)
                {
                    Reads++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentQueueSession(null)) };
                }
                Requests.Add((await request.Content!.ReadFromJsonAsync<AgentQueueSessionRequest>(cancellationToken: token))!);
                if (Requests.Count == 1) throw new HttpRequestException("The server committed the session, but the response was lost.");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Content is null) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(++reports == 1 ? HttpStatusCode.NoContent : HttpStatusCode.Conflict);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<AgentHeartbeatReport> Reports { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/queue-session")
                return request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentQueueSession(null)) }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            Assert.AreEqual("/api/v1/agent/heartbeat", request.RequestUri!.AbsolutePath);
            Reports.Add((await request.Content!.ReadFromJsonAsync<AgentHeartbeatReport>(cancellationToken: token))!);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class LegacyHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            if (Count == 1)
            {
                Assert.AreEqual("/api/v1/agent/queue-session", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            Assert.IsNull(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
