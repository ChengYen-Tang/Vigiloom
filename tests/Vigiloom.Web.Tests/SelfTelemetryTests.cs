using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Vigiloom.Infrastructure.Telemetry;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class SelfTelemetryTests(TestContext context)
{
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ExportsTracesToInternalOtlpEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vigiloom:SelfTelemetry:Url"] = $"http://127.0.0.1:{port}", ["Vigiloom:SelfTelemetry:Enabled"] = "true"
        });
        builder.AddVigiloomSelfTelemetry("Vigiloom.Web");
        using var host = builder.Build();
        await host.StartAsync(context.CancellationToken);
        using var client = new HttpClient();
        var work = client.GetAsync($"http://127.0.0.1:{port}/work", context.CancellationToken);
        Assert.StartsWith("GET /work ", await RespondAsync(listener, context.CancellationToken));
        using var response = await work;
        response.EnsureSuccessStatusCode();
        var flush = Task.Run(() => host.Services.GetRequiredService<TracerProvider>().ForceFlush(10000), context.CancellationToken);
        var path = await RespondAsync(listener, context.CancellationToken);
        Assert.StartsWith("POST /internal/v1/self-otlp/v1/traces ", path);
        Assert.IsTrue(await flush);
        await StopHostAsync(host, listener, context.CancellationToken);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task IdleServiceExportsSelfMetricToConfiguredObservationService()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Observability:Url"] = $"http://127.0.0.1:{port}", ["Vigiloom:SelfTelemetry:Enabled"] = "true" });
        builder.AddVigiloomSelfTelemetry("Vigiloom.Worker");
        using var host = builder.Build();
        await host.StartAsync(context.CancellationToken);
        var flush = Task.Run(() => host.Services.GetRequiredService<MeterProvider>().ForceFlush(10000), context.CancellationToken);
        Assert.StartsWith("POST /internal/v1/self-otlp/v1/metrics ", await RespondAsync(listener, context.CancellationToken));
        Assert.IsTrue(await flush);
        await StopHostAsync(host, listener, context.CancellationToken);
    }

    [TestMethod]
    public void SelfTelemetryCanBeDisabled()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Vigiloom:SelfTelemetry:Enabled"] = "false" });
        builder.AddVigiloomSelfTelemetry("Vigiloom.Worker");
        using var host = builder.Build();
        Assert.IsNull(host.Services.GetService<MeterProvider>());
    }

    private static async Task StopHostAsync(IHost host, TcpListener listener, CancellationToken token)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        var responder = Task.Run(async () =>
        {
            try
            {
                while (!stopping.IsCancellationRequested) await RespondAsync(listener, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (SocketException) when (stopping.IsCancellationRequested) { }
        }, CancellationToken.None);
        try
        {
            await host.StopAsync(token);
            host.Dispose();
        }
        finally
        {
            await stopping.CancelAsync();
            listener.Stop();
            await responder;
        }
    }

    private static async Task<string> RespondAsync(TcpListener listener, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var request = await reader.ReadLineAsync(token) ?? throw new InvalidOperationException("Missing HTTP request.");
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        return request;
    }
}
