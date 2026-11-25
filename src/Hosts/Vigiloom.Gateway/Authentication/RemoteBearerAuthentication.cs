using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Vigiloom.Gateway.Authentication;

internal abstract class RemoteBearerAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IHttpClientFactory clients) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private bool throttled;
    private string? retryAfter;
    protected abstract string ClientName { get; }
    protected abstract string IdentityPath { get; }
    protected virtual bool VerifyRejectedRequests => false;
    protected virtual void ConfigureRequest(HttpRequestMessage request) { }
    protected abstract Task<Claim[]?> ReadIdentityAsync(HttpResponseMessage response, CancellationToken token);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hasBearer = AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var authorization) &&
            string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(authorization.Parameter);
        if (!hasBearer && !VerifyRejectedRequests) return AuthenticateResult.NoResult();
        if (!Request.IsHttps && !VerifyRejectedRequests) return AuthenticateResult.Fail("A secure connection is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, IdentityPath);
        if (hasBearer) request.Headers.Authorization = authorization;
        ConfigureRequest(request);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", Request.Scheme);
        if (Context.Connection.RemoteIpAddress is { } address)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", address.ToString());
        using var response = await clients.CreateClient(ClientName).SendAsync(request, Context.RequestAborted);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return AuthenticateResult.Fail("Invalid credential.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throttled = true;
            retryAfter = response.Headers.RetryAfter?.ToString();
            return AuthenticateResult.Fail("Identity service rate limit exceeded.");
        }
        response.EnsureSuccessStatusCode();
        if (!Request.IsHttps || !hasBearer) return AuthenticateResult.Fail("A secure connection and bearer credential are required.");
        var claims = await ReadIdentityAsync(response, Context.RequestAborted);
        if (claims is null) return AuthenticateResult.Fail("Invalid identity response.");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ClientName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ClientName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!throttled) return base.HandleChallengeAsync(properties);
        Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (retryAfter is not null) Response.Headers.RetryAfter = retryAfter;
        return Task.CompletedTask;
    }
}
