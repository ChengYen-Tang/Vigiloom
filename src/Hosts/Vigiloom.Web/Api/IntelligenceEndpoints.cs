using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class IntelligenceEndpoints
{
    public static void MapIntelligenceEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin/intelligence/sources")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/", async (int? page, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var selected = page ?? 1;
            return selected is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await status.GetSourcesAsync(selected, token));
        }).Produces<IntelligenceSourcePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List intelligence source status");
        admin.MapGet("/{sourceId:guid}/revisions", async (Guid sourceId, int? page, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var selected = page ?? 1;
            if (selected is < 1 or > 1000) return Results.BadRequest();
            return await status.GetRevisionsAsync(sourceId, selected, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<IntelligenceRevisionPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List retained intelligence revisions");
        admin.MapGet("/{sourceId:guid}/advisory-progress", async (Guid sourceId, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await status.GetAdvisoryProgressAsync(sourceId, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<AdvisoryImportProgressView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read GitHub advisory import progress and coverage");
        admin.MapPost("/{sourceId:guid}/advisory-recovery", async (Guid sourceId, AdvisoryImportRecoveryRequest input, AdvisoryRecovery recovery, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || input.ImportId.Version != 7) return Results.ValidationProblem(new Dictionary<string, string[]> { ["import"] = ["Invalid import identity."] });
            return await ApiMutation.ExecuteAsync(context, async actor => (await recovery.AbandonAsync(sourceId, input, actor, token)) switch
            {
                AdvisoryRecoveryResult.Abandoned => Results.NoContent(),
                AdvisoryRecoveryResult.NotFound => Results.NotFound(),
                _ => Results.Problem(statusCode: 409, title: "The import changed or cannot be restarted; reload and try again.")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Abandon a failed advisory import so a later scheduled refresh can start again");
        admin.MapPut("/{sourceId:guid}/policy", async (Guid sourceId, IntelligencePolicyUpdate input, IntelligencePolicy policy, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || !IntelligencePolicy.Valid(input)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["Invalid source policy."] });
            return await ApiMutation.ExecuteAsync(context, async actor => (await policy.UpdateAsync(sourceId, input, actor, token)) switch
            {
                IntelligencePolicyResult.Updated or IntelligencePolicyResult.Unchanged => Results.NoContent(),
                IntelligencePolicyResult.NotFound => Results.NotFound(),
                _ => Results.Problem(statusCode: 409, title: "Source settings changed; reload and try again.")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Update intelligence source policy");
        admin.MapPost("/{sourceId:guid}/activation", async (Guid sourceId, IntelligenceActivationRequest input, IntelligencePublication publication, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || input.RevisionId == Guid.Empty || input.ExpectedActiveRevisionId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["revision"] = ["Invalid source or revision identity."] });
            return await ApiMutation.ExecuteAsync(context, async actor => (await publication.ActivateAsync(sourceId, input.RevisionId,
                input.ExpectedActiveRevisionId, input.ExpectedVersion, input.ConfirmLargeReduction, actor, token)) switch
            {
                IntelligencePublicationResult.Published or IntelligencePublicationResult.Unchanged => Results.NoContent(),
                IntelligencePublicationResult.NotFound => Results.NotFound(),
                IntelligencePublicationResult.ConfirmationRequired => Results.Problem(statusCode: 409, title: "A large reduction requires explicit confirmation.", type: "urn:vigiloom:intelligence:confirmation-required"),
                IntelligencePublicationResult.Unavailable => Results.Problem(statusCode: 409, title: "The source or revision is unavailable.", type: "urn:vigiloom:intelligence:unavailable"),
                _ => Results.Problem(statusCode: 409, title: "The active revision changed; reload and try again.", type: "urn:vigiloom:intelligence:conflict")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Activate or restore a retained intelligence revision");
    }
}
