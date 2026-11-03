using Vigiloom.Web.Authentication;
using Vigiloom.Web.Diagnostics;

namespace Vigiloom.Web.Api;

internal static class PlatformStatusEndpoints
{
    public static void MapPlatformStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/operations/platform-status", async (PlatformStatus status, CancellationToken token) =>
            Results.Ok(await status.GetAsync(token)))
            .RequireAuthorization(AccessPolicies.Administrator)
            .Produces<PlatformStatusSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get administrator platform health observations");
    }
}
