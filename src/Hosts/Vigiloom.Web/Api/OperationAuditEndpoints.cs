using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Web.Diagnostics;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class OperationAuditEndpoints
{
    public static void MapOperationAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/operations/audit/audit-retention", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryAuditRetentionAsync(query, token)) : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query audit retention settings changes");
        app.MapGet("/api/v1/settings/audit-retention", async (AuditRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization(AccessPolicies.Administrator).Produces<AuditRetentionSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get settings audit retention");
        app.MapPost("/api/v1/settings/audit-retention/preview", async (AuditRetentionPreviewRequest input, HttpContext context, AuditRetentionSettings settings, CancellationToken token) =>
        {
            if (!AuditRetentionLimits.IsValid(input.Days)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Conflict(), allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<AuditRetentionPreview>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Preview settings audit retention");
        app.MapPut("/api/v1/settings/audit-retention", async (AuditRetentionInput input, HttpContext context, AuditRetentionSettings settings, CancellationToken token) =>
        {
            if (!AuditRetentionLimits.IsValid(input.Days)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input, actor, token) is { } saved ? Results.Ok(saved) : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required."] }); }
            }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<AuditRetentionSnapshot>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Update settings audit retention");
        app.MapGet("/api/v1/operations/audit/line", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, HttpContext context, CancellationToken token, int page = 1) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryLineAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query LINE notification setting changes");
        app.MapGet("/api/v1/operations/audit/intelligence", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, HttpContext context, CancellationToken token, int page = 1) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryIntelligenceAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query intelligence management audit events");
        app.MapGet("/api/v1/operations/audit/publications", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryPublicationsAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query publication changes");
        app.MapGet("/api/v1/operations/audit/agent-inbox", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryAgentInboxAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/operations/audit/health-networks", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryHealthNetworkPoliciesAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/operations/audit/health-targets", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryHealthTargetsAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/operations/audit/trace-quota", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryTraceQuotaAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/operations/audit/log-quota", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryLogQuotaAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/operations/audit/service-collections", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryServiceCollectionsAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/operations/audit/instance-policy", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryInstancePoliciesAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/operations/audit/service-instances", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query) ? Results.Ok(await audit.QueryServiceInstancesAsync(query, token)) : Results.BadRequest();
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<OperationAuditPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
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
        app.MapGet("/api/v1/operations/audit/agent-replay", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query)
                ? Results.Ok(await audit.QueryAgentReplayAsync(query, token))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator)
            .Produces<OperationAuditPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query Agent replay policy audit events");
        app.MapGet("/api/v1/operations/audit/service-approvals", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query)
                ? Results.Ok(await audit.QueryServiceApprovalsAsync(query, token))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator)
            .Produces<OperationAuditPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query application service approval audit events");
        app.MapGet("/api/v1/operations/audit/service-sources", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query)
                ? Results.Ok(await audit.QueryServiceSourcesAsync(query, token))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator)
            .Produces<OperationAuditPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query service source credential audit events");
        app.MapGet("/api/v1/operations/audit/groups", async (DateTimeOffset from, DateTimeOffset to,
            OperationAuditQueryService audit, CancellationToken token, int page = 1) =>
        {
            var query = new OperationAuditQuery(from, to, page);
            return OperationAuditQueryService.Valid(query)
                ? Results.Ok(await audit.QueryGroupsAsync(query, token))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid page."] });
        }).RequireAuthorization(AccessPolicies.Administrator)
            .Produces<OperationAuditPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Query group operation audit events");
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
