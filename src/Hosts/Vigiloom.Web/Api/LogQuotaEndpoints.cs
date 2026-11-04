using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.LogQuota;
using LogQuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

namespace Vigiloom.Web.Api;

internal static class LogQuotaEndpoints
{
    public static void MapLogQuotaEndpoints(this WebApplication app)
    {
        var trace = app.MapGroup("/api/v1/trace-quota").RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        trace.MapGet("/", async (TraceQuotaSettings settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(Map(await settings.GetAsync(token)));
        }).Produces<LogQuotaContract>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        trace.MapPut("/", async (LogQuotaContract input, HttpContext context, TraceQuotaSettings settings, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var values = new LogQuotaValues(input.SourceRate, input.SourceBurst, input.GlobalRate, input.GlobalBurst);
                if (!values.IsValid) return Results.BadRequest();
                var saved = await settings.UpdateAsync(values, input.Version, actor, token);
                return saved is null ? Results.Conflict() : Results.Ok(Map(saved));
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<LogQuotaContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        var route = app.MapGroup("/api/v1/log-quota").RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        route.MapGet("/", async (LogQuotaSettings settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(Map(await settings.GetAsync(token)));
        }).Produces<LogQuotaContract>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        route.MapPut("/", async (LogQuotaContract input, HttpContext context, LogQuotaSettings settings, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var values = new LogQuotaValues(input.SourceRate, input.SourceBurst, input.GlobalRate, input.GlobalBurst);
                if (!values.IsValid) return Results.BadRequest();
                var saved = await settings.UpdateAsync(values, input.Version, actor, token);
                return saved is null ? Results.Conflict() : Results.Ok(Map(saved));
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<LogQuotaContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
    }

    private static LogQuotaContract Map(LogQuotaSnapshot value) =>
        new(value.Values.SourceRate, value.Values.SourceBurst, value.Values.GlobalRate, value.Values.GlobalBurst, value.Version);
}
