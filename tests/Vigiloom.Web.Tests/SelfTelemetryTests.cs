using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
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
            ["Vigiloom:SelfTelemetry:Url"] = $"http://127.0.0.1:{port}"
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
        await host.StopAsync(context.CancellationToken);
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
