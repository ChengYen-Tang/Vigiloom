using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.SecurityAnalysis.Application.Baseline;

namespace Vigiloom.Security;

internal static class BaselineEndpoints
{
    internal static void MapPublicBaseline(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/baseline", async (Guid deviceId, BaselineQuery query, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await query.GetAsync(deviceId, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");

        app.MapPost("/api/v1/admin/devices/{deviceId:guid}/baseline-exceptions", async (Guid deviceId, BaselineExceptionInput input,
            IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var exceptions = services.GetService<BaselineExceptions>();
                if (exceptions is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                return (await exceptions.RecordAsync(deviceId, input.SourceKey, input.RevisionId, input.Reason, actor, token)) switch
                {
                    BaselineExceptionStatus.Recorded => Results.NoContent(),
                    BaselineExceptionStatus.NotFound => Results.NotFound(),
                    BaselineExceptionStatus.VersionChanged => Results.Problem(statusCode: 409, title: "The selected baseline revision is no longer active."),
                    _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Invalid baseline exception explanation."] })
                };
            }, allowApiCredential: false);
        }).RequireAuthorization("Administrator").RequireRateLimiting("api-client");
    }
}
