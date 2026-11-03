using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Web.Diagnostics;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class OperationAuditEndpoints
{
    public static void MapOperationAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/operations/audit-feed/{source}", async (string source, DateTimeOffset from, DateTimeOffset to,
            AuditFeedReader feeds, CancellationToken token, int page = 1) =>
        {
            var selected = AuditFeedReader.Parse(source);
            var query = new OperationAuditQuery(from, to, page);
            return selected is { } value && AuditFeedLimits.Valid(query)
                ? Results.Ok(await feeds.QueryAsync(value, query, token))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Use a supported source, UTC range of at most 31 days, and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator)
            .Produces<AuditFeedPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query a single authorized operation audit source");
        app.MapGet("/api/v1/operations/audit/ping", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query)
                ? Results.Ok(await audit.QueryPingAsync(query, token))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator)
            .Produces<OperationAuditPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query Ping settings audit events");
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
