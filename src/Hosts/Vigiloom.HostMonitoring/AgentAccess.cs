using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.HostMonitoring;

internal sealed class AgentAccess(RequestDelegate next, IHttpClientFactory clients, ILogger<AgentAccess> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        RegisteredAgentIdentity? identity;
        try
        {
            using var response = await clients.CreateClient("resources").PostAsJsonAsync("internal/v1/agents/authenticate",
                new AgentCredentialCommand(header[7..]), context.RequestAborted);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            response.EnsureSuccessStatusCode();
            identity = await response.Content.ReadFromJsonAsync<RegisteredAgentIdentity>(cancellationToken: context.RequestAborted);
            if (identity is null || identity.AgentId.Version != 7 || identity.DeviceId.Version != 7 || identity.DeviceGeneration < 0)
                throw new InvalidOperationException("Invalid Agent identity response.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Agent identity service unavailable: {ErrorType}.", error.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        context.Items[typeof(RegisteredAgentIdentity)] = identity;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, identity.AgentId.ToString("D"))], "Agent"));
        await next(context);
    }
}
