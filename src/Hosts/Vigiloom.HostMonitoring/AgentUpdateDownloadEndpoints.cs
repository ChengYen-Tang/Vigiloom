using System.Net;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.HostMonitoring;

internal static class AgentUpdateDownloadEndpoints
{
    public static void MapAgentUpdateDownloadEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/update/targets/{name}",
            (string name, HttpContext context, IHttpClientFactory clients, ILoggerFactory logs, CancellationToken token) =>
                ForwardAsync("targets", name, context, clients, logs, token));
        app.MapGet("/api/v1/agent/update/packages/{name}",
            (string name, HttpContext context, IHttpClientFactory clients, ILoggerFactory logs, CancellationToken token) =>
                ForwardAsync("packages", name, context, clients, logs, token));
    }

    private static async Task ForwardAsync(string kind, string name, HttpContext context,
        IHttpClientFactory clients, ILoggerFactory logs, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var identity = (RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!;
        var path = $"internal/v1/agents/{identity.AgentId:D}/update/{kind}/{Uri.EscapeDataString(name)}";
        try
        {
            using var response = await clients.CreateClient("resources").GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            response.EnsureSuccessStatusCode();
            context.Response.ContentType = kind == "targets" ? "application/json" : "application/octet-stream";
            await response.Content.CopyToAsync(context.Response.Body, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            logs.CreateLogger("AgentUpdateDownload").LogWarning("Agent update package unavailable: {ErrorType}", error.GetType().Name);
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }
}
