using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;

namespace Vigiloom.Gateway.Authentication;

internal sealed class AgentTransportLimits : IDisposable
{
    private readonly ConcurrencyLimiter concurrent;
    private readonly PartitionedRateLimiter<string> perAgent;

    public AgentTransportLimits(IConfiguration configuration)
    {
        concurrent = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:MaxConcurrentRequests", 128), 1, 1024),
            QueueLimit = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:MaxQueuedRequests", 512), 0, 4096),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        var limit = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:RequestsPerMinute", 120), 1, 10000);
        perAgent = PartitionedRateLimiter.Create<string, string>(id => RateLimitPartition.GetFixedWindowLimiter(id, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        using var slot = await concurrent.AcquireAsync(1, context.RequestAborted);
        if (!slot.IsAcquired) { Reject(context, "5"); return; }
        var authentication = await context.AuthenticateAsync(AgentAuthentication.SchemeName);
        if (!authentication.Succeeded || authentication.Principal is null)
        {
            await context.ChallengeAsync(AgentAuthentication.SchemeName);
            return;
        }
        context.User = authentication.Principal;
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        using var lease = await perAgent.AcquireAsync(id, 1, context.RequestAborted);
        if (!lease.IsAcquired) { Reject(context, "60"); return; }
        await next(context);
    }

    private static void Reject(HttpContext context, string retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfter;
    }

    public void Dispose() { concurrent.Dispose(); perAgent.Dispose(); }
}
