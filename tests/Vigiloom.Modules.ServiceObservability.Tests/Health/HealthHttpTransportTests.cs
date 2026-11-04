using System.Net;
using System.Net.Sockets;
using System.Text;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthHttpTransportTests
{
    [TestMethod]
    [DataRow("192.168.10.23", true)]
    [DataRow("::ffff:192.168.10.23", true)]
    [DataRow("192.168.11.1", false)]
    [DataRow("169.254.169.254", false)]
    [DataRow("::1", false)]
    public void IsAllowed_UsesConfiguredNetworkAfterMappedAddressNormalization(string address, bool expected)
    {
        var networks = new[] { IPNetwork.Parse("192.168.10.0/24") };
        Assert.AreEqual(expected, HealthHttpTransport.IsAllowed(IPAddress.Parse(address), networks));
    }

    [TestMethod]
    public void CreateHandler_DisablesAutomaticRedirectsProxiesAndCookies()
    {
        using var handler = HealthHttpTransport.CreateHandler([]);
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.IsFalse(handler.UseProxy);
        Assert.IsFalse(handler.UseCookies);
    }

    [TestMethod]
    public async Task StaleTransportGeneration_IsNotReportedAsNetworkFailure()
    {
        using var pool = new HealthTransportPool();
        pool.ApplyPolicy(1, [IPNetwork.Parse("127.0.0.0/8")]);
        pool.ApplyPolicy(2, []);
        var result = await pool.ReadAsync(1, new Uri("http://127.0.0.1:1/health"), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.AreEqual(1L, result.PolicyVersion);
        Assert.AreEqual(HealthProbeFailure.PolicyChanged, result.Result.Failure);
    }

    [TestMethod]
    public async Task Probe_ConnectsOnlyWhenResolvedAddressIsAllowed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = ReplyOnceAsync(listener, timeout.Token);
            using var client = new HttpClient(HealthHttpTransport.CreateHandler([IPNetwork.Parse("127.0.0.0/8")]));
            var result = await new HealthProbe(client).ReadAsync(new Uri($"http://localhost:{port}/health"), TimeSpan.FromSeconds(5), timeout.Token);
            await server;
            Assert.AreEqual(ReportedHealth.Healthy, result.Health);
            Assert.AreEqual(HealthProbeFailure.None, result.Failure);

            using var blocked = new HttpClient(HealthHttpTransport.CreateHandler([]));
            var rejected = await new HealthProbe(blocked).ReadAsync(new Uri($"http://localhost:{port}/health"), TimeSpan.FromSeconds(5), timeout.Token);
            Assert.AreEqual(HealthProbeFailure.NetworkDenied, rejected.Failure);
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task ReplacingPolicy_RetiresPooledConnectionBeforeNextRequest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<bool>? server = null;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            server = ReplyKeepAliveAsync(listener, timeout.Token);
            using var pool = new HealthTransportPool();
            pool.ApplyPolicy(1, [IPNetwork.Parse("127.0.0.0/8")]);
            var endpoint = new Uri($"http://localhost:{port}/health");
            var first = await pool.ReadAsync(1, endpoint, TimeSpan.FromSeconds(5), timeout.Token);
            Assert.AreEqual(ReportedHealth.Healthy, first.Result.Health);
            pool.ApplyPolicy(2, []);
            var second = await pool.ReadAsync(2, endpoint, TimeSpan.FromSeconds(5), timeout.Token);
            Assert.AreEqual(2L, second.PolicyVersion);
            Assert.AreEqual(HealthProbeFailure.NetworkDenied, second.Result.Failure);
            Assert.IsFalse(await server, "A retired connection must not carry a second request.");
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            if (server is not null)
                try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
    }

    [TestMethod]
    public async Task ReplacingPolicy_CancelsAnIncompleteResponse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? server = null;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            server = ReplyIncompleteAsync(listener, headersSent, timeout.Token);
            using var pool = new HealthTransportPool();
            pool.ApplyPolicy(1, [IPNetwork.Parse("127.0.0.0/8")]);
            var probe = pool.ReadAsync(1, new Uri($"http://localhost:{port}/health"), TimeSpan.FromSeconds(5), timeout.Token);
            await headersSent.Task.WaitAsync(timeout.Token);
            pool.ApplyPolicy(2, []);
            var result = await probe;
            Assert.AreEqual(1L, result.PolicyVersion);
            Assert.AreEqual(HealthProbeFailure.PolicyChanged, result.Result.Failure);
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            if (server is not null)
                try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
    }

    [TestMethod]
    public async Task CallerCancellation_DuringIncompleteResponse_RemainsCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? server = null;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            server = ReplyIncompleteAsync(listener, headersSent, timeout.Token);
            using var pool = new HealthTransportPool();
            pool.ApplyPolicy(1, [IPNetwork.Parse("127.0.0.0/8")]);
            var probe = pool.ReadAsync(1, new Uri($"http://localhost:{port}/health"), TimeSpan.FromSeconds(5), caller.Token);
            await headersSent.Task.WaitAsync(timeout.Token);
            caller.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await probe);
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            if (server is not null)
                try { await server; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
    }

    private static async Task ReplyOnceAsync(TcpListener listener, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nHealthy"u8.ToArray(), token);
    }

    private static async Task<bool> ReplyKeepAliveAsync(TcpListener listener, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: keep-alive\r\n\r\nHealthy"u8.ToArray(), token);
        return await reader.ReadLineAsync(token) is not null;
    }

    private static async Task ReplyIncompleteAsync(TcpListener listener, TaskCompletionSource headersSent, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        await using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\n\r\n"u8.ToArray(), token);
        headersSent.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
