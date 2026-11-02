using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class OperationAuditEndpoints
{
    public static void MapOperationAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/operations/audit", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query)
                ? Results.Ok(await audit.QueryAsync(query, token))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator)
            .Produces<OperationAuditPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query authorized operation audit events");
    }
}
