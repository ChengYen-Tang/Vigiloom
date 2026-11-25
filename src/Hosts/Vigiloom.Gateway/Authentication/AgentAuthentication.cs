using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Vigiloom.Gateway.Authentication;

/// <summary>Checks Agent credentials with the service that owns Agent identity.</summary>
internal sealed class AgentAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IHttpClientFactory clients) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GatewayAgent";
    private bool throttled;
    private string? retryAfter;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter)) return AuthenticateResult.NoResult();
        if (!Request.IsHttps) return AuthenticateResult.Fail("A secure connection is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/agent/identity");
        request.Headers.Authorization = authorization;
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", Request.Scheme);
        if (Context.Connection.RemoteIpAddress is { } address)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", address.ToString());
        using var response = await clients.CreateClient(SchemeName).SendAsync(request, Context.RequestAborted);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return AuthenticateResult.Fail("Invalid Agent credential.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throttled = true;
            retryAfter = response.Headers.RetryAfter?.ToString();
            return AuthenticateResult.Fail("Agent identity service rate limit exceeded.");
        }
        response.EnsureSuccessStatusCode();
        var agent = await response.Content.ReadFromJsonAsync<AgentIdentity>(cancellationToken: Context.RequestAborted);
        if (agent is null || agent.AgentId == Guid.Empty || agent.DeviceId == Guid.Empty)
            return AuthenticateResult.Fail("Invalid Agent identity response.");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, agent.AgentId.ToString("D")),
            new Claim("vigiloom:device-id", agent.DeviceId.ToString("D"))
        ], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!throttled) return base.HandleChallengeAsync(properties);
        Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (retryAfter is not null) Response.Headers.RetryAfter = retryAfter;
        return Task.CompletedTask;
    }

    private sealed record AgentIdentity(Guid AgentId, Guid DeviceId);
}
