using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;

namespace Vigiloom.Web.Authentication;

/// <summary>Marks machine transport endpoints that must authenticate before their per-Agent rate limit.</summary>
internal sealed class AgentTransportEndpoint
{
    public static AgentTransportEndpoint Instance { get; } = new();
    private AgentTransportEndpoint() { }
}

/// <summary>Bounds concurrent machine transport work before performing credential verification.</summary>
internal sealed class AgentTransportConcurrency : IDisposable
{
    private readonly ConcurrencyLimiter limiter;

    public AgentTransportConcurrency(IConfiguration configuration)
    {
        limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:MaxConcurrentRequests", 128), 1, 1024),
            QueueLimit = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:MaxQueuedRequests", 512), 0, 4096),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken token) => limiter.AcquireAsync(1, token);
    public void Dispose() => limiter.Dispose();
}

internal sealed class AgentTransportGuard(RequestDelegate next, AgentTransportConcurrency concurrency)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AgentTransportEndpoint>() is null)
        {
            await next(context);
            return;
        }

        using var lease = await concurrency.AcquireAsync(context.RequestAborted);
        if (!lease.IsAcquired)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "5";
            return;
        }
        var authentication = await context.AuthenticateAsync(AgentCredentialHandler.SchemeName);
        if (!authentication.Succeeded || authentication.Principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.User = authentication.Principal;
        await next(context);
    }
}
