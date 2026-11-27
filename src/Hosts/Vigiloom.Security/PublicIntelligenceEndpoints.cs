using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Infrastructure.Authentication;

namespace Vigiloom.Security;

internal static class PublicIntelligenceEndpoints
{
    public static void MapPublicIntelligenceEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin/intelligence/sources")
            .RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        admin.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/baseline-check", async (Guid sourceId, Guid revisionId,
            CustomBaselineStage stage, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await stage.ReadAsync(sourceId, revisionId, token) is { } check ? Results.Ok(check) : Results.NotFound();
        }).Produces<BaselineCheck>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read a locally authored baseline check before review");
        admin.MapPost("/baseline-checks", async (CustomBaselineDraftRequest input, CustomBaselineStage stage, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => StageResult(await stage.SaveAsync(null, null, input.Check, actor, token)), allowApiCredential: false);
        }).Produces<CustomBaselineStageResult>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Stage a locally authored baseline check");
        admin.MapPost("/{sourceId:guid}/baseline-revisions", async (Guid sourceId, CustomBaselineDraftRequest input, CustomBaselineStage stage, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await ApiMutation.ExecuteAsync(context, async actor => StageResult(await stage.SaveAsync(sourceId, input.ExpectedVersion, input.Check, actor, token)), allowApiCredential: false);
        }).Produces<CustomBaselineStageResult>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Stage a revised local baseline check");
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
        admin.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/sigma-rules", async (Guid sourceId, Guid revisionId, int? page, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var selected = page ?? 1;
            if (selected is < 1 or > 1000) return Results.BadRequest();
            return await status.GetSigmaRulesAsync(sourceId, revisionId, selected, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<SigmaRulePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List retained Sigma rule provenance");
        admin.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/signature-files", async (Guid sourceId, Guid revisionId, int? page, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var selected = page ?? 1;
            if (selected is < 1 or > 1000) return Results.BadRequest();
            return await status.GetSignatureFilesAsync(sourceId, revisionId, selected, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<SignatureFilePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List retained IOC and YARA source-file metadata");
        admin.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/signature-evidence", async (Guid sourceId, Guid revisionId, string? path, int? line, int? page, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (path is null || path.Length is < 1 or > 512 || line is < 1 or > 1048576 || page is < 1 or > 2000) return Results.BadRequest();
            return await status.GetSignatureEvidenceAsync(sourceId, revisionId, path, token, line, page ?? 1) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<SignatureEvidenceView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read bounded candidate author and license lines from a retained source file");
        admin.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/ioc-issues", async (Guid sourceId, Guid revisionId, string? path, int? page, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var selected = page ?? 1;
            if (selected is < 1 or > 2000 || path is null || path.Length is < 1 or > 512) return Results.BadRequest();
            return await status.GetIocIssuesAsync(sourceId, revisionId, path, selected, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<IocIssuePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List unsupported IOC line locations and reasons");
        admin.MapGet("/{sourceId:guid}/advisory-progress", async (Guid sourceId, IntelligenceStatus status, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await status.GetAdvisoryProgressAsync(sourceId, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<AdvisoryImportProgressView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read GitHub advisory import progress and coverage");
        admin.MapPost("/{sourceId:guid}/advisory-recovery", async (Guid sourceId, AdvisoryImportRecoveryRequest input, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || input.ImportId.Version != 7) return Results.ValidationProblem(new Dictionary<string, string[]> { ["import"] = ["Invalid import identity."] });
            var recovery = services.GetService<AdvisoryRecovery>();
            if (recovery is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await ApiMutation.ExecuteAsync(context, async actor => (await recovery.AbandonAsync(sourceId, input, actor, token)) switch
            {
                AdvisoryRecoveryResult.Abandoned => Results.NoContent(),
                AdvisoryRecoveryResult.NotFound => Results.NotFound(),
                _ => Results.Problem(statusCode: 409, title: "The import changed or cannot be restarted; reload and try again.")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Abandon a failed advisory import so a later scheduled refresh can start again");
        admin.MapPut("/{sourceId:guid}/advisory-credential", async (Guid sourceId, AdvisoryCredentialUpdate input, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || !AdvisoryCredentials.Valid(input)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = ["Invalid credential settings."] });
            var credentials = services.GetService<AdvisoryCredentials>();
            if (credentials is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await ApiMutation.ExecuteAsync(context, async actor => (await credentials.UpdateAsync(sourceId, input, actor, token)) switch
            {
                AdvisoryCredentialResult.Updated or AdvisoryCredentialResult.Unchanged => Results.NoContent(),
                AdvisoryCredentialResult.NotFound => Results.NotFound(),
                _ => Results.Problem(statusCode: 409, title: "Source settings changed; reload and try again.")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Replace or clear the GitHub advisory source credential");
        admin.MapPut("/{sourceId:guid}/policy", async (Guid sourceId, IntelligencePolicyUpdate input, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || !IntelligencePolicy.Valid(input)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["Invalid source policy."] });
            var policy = services.GetService<IntelligencePolicy>();
            if (policy is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await ApiMutation.ExecuteAsync(context, async actor => (await policy.UpdateAsync(sourceId, input, actor, token)) switch
            {
                IntelligencePolicyResult.Updated or IntelligencePolicyResult.Unchanged => Results.NoContent(),
                IntelligencePolicyResult.NotFound => Results.NotFound(),
                IntelligencePolicyResult.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["The limits exceed those supported by this source."] }),
                _ => Results.Problem(statusCode: 409, title: "Source settings changed; reload and try again.")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Update intelligence source policy");
        admin.MapPost("/{sourceId:guid}/activation", async (Guid sourceId, IntelligenceActivationRequest input, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || input.RevisionId == Guid.Empty || input.ExpectedActiveRevisionId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["revision"] = ["Invalid source or revision identity."] });
            var publication = services.GetService<IntelligencePublication>();
            if (publication is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await ApiMutation.ExecuteAsync(context, async actor => (await publication.ActivateAsync(sourceId, input.RevisionId,
                input.ExpectedActiveRevisionId, input.ExpectedVersion, input.ConfirmLargeReduction, input.ConfirmOfflineSource, actor, token)) switch
            {
                IntelligencePublicationResult.Published or IntelligencePublicationResult.Unchanged => Results.NoContent(),
                IntelligencePublicationResult.NotFound => Results.NotFound(),
                IntelligencePublicationResult.ConfirmationRequired => Results.Problem(statusCode: 409, title: "A large reduction requires explicit confirmation.", type: "urn:vigiloom:intelligence:confirmation-required"),
                IntelligencePublicationResult.OfflineConfirmationRequired => Results.Problem(statusCode: 409, title: "An offline source requires explicit provenance confirmation.", type: "urn:vigiloom:intelligence:offline-confirmation-required"),
                IntelligencePublicationResult.Unavailable => Results.Problem(statusCode: 409, title: "The source or revision is unavailable.", type: "urn:vigiloom:intelligence:unavailable"),
                _ => Results.Problem(statusCode: 409, title: "The active revision changed; reload and try again.", type: "urn:vigiloom:intelligence:conflict")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Activate or restore a retained intelligence revision");
        admin.MapPost("/{sourceId:guid}/revisions/{revisionId:guid}/revoke", async (Guid sourceId, Guid revisionId, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (sourceId == Guid.Empty || revisionId.Version != 7)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["revision"] = ["Invalid source or revision identity."] });
            var revocation = services.GetService<IntelligenceRevocation>();
            if (revocation is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await ApiMutation.ExecuteAsync(context, async actor => (await revocation.RevokeAsync(sourceId, revisionId, actor, token)) switch
            {
                IntelligenceRevocationResult.Revoked or IntelligenceRevocationResult.AlreadyRevoked => Results.NoContent(),
                _ => Results.NotFound()
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Revoke a retained intelligence revision");
    }

    private static IResult StageResult(CustomBaselineStageResult result) => result.Status switch
    {
        CustomBaselineStageStatus.Staged or CustomBaselineStageStatus.Unchanged => Results.Ok(result),
        CustomBaselineStageStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["check"] = ["Invalid or unsupported baseline check."] }),
        CustomBaselineStageStatus.NotFound => Results.NotFound(),
        _ => Results.Problem(statusCode: 409, title: "The check could not be staged; reload and review its status.")
    };
}

/// <summary>A local read-only check to stage, plus the source version when revising an existing check.</summary>
internal sealed record CustomBaselineDraftRequest(BaselineCheck Check, string? ExpectedVersion = null);
