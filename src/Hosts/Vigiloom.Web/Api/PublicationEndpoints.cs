using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Publishing;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Web.Api;

internal static class PublicationEndpoints
{
    public static void MapPublicationEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin/publications").RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapPost("/templates", async (CreatePublicationTemplate input, TemplateAdministration templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await templates.CreateAsync(input, actor, token);
                return result.Status switch
                {
                    TemplateChangeStatus.Created => Results.Ok(result.Template),
                    TemplateChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["請輸入 1 至 256 個字元的範本名稱。"] }),
                    _ => Results.Problem(statusCode: 409, title: "The template name is already in use or the settings are being changed.")
                };
            }, allowApiCredential: false);
        }).Produces<PublicationTemplateItem>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Create a publication template");
        admin.MapPut("/templates/{id:guid}/default", async (Guid id, SetDefaultPublicationTemplate input, TemplateAdministration templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await templates.SetDefaultAsync(id, input, actor, token);
                return result.Status switch
                {
                    TemplateChangeStatus.Updated => Results.Ok(result.Template),
                    TemplateChangeStatus.NotFound => Results.NotFound(),
                    TemplateChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["請重新整理後再試。"] }),
                    _ => Results.Problem(statusCode: 409, title: "The template or default selection changed; refresh and try again.")
                };
            }, allowApiCredential: false);
        }).Produces<PublicationTemplateItem>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Select the default publication template");
        admin.MapGet("/templates", async (int? page, TemplateReader templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return page is < 1 or > TemplateReader.MaximumPage ? Results.BadRequest() : Results.Ok(await templates.ListAsync(page ?? 1, token));
        }).Produces<PublicationTemplatePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List publication templates");
        admin.MapGet("/templates/default", async (TemplateReader templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await templates.DefaultAsync(token) is { } template ? Results.Ok(template) : Results.NotFound();
        }).Produces<PublicationTemplateItem>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get the current default publication template");
        admin.MapGet("/templates/{id:guid}", async (Guid id, TemplateReader templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await templates.GetAsync(id, token) is { } template ? Results.Ok(template) : Results.NotFound();
        }).Produces<PublicationTemplateItem>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get a publication template");
        admin.MapPost("/templates/{id:guid}/update-preview", async (Guid id, PublicationTemplateUpdate input, TemplateUpdateWorkflow workflow, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await workflow.PreviewAsync(id, input, actor, token);
                return result.Status switch
                {
                    TemplateChangeStatus.Updated => Results.Ok(result.Preview),
                    TemplateChangeStatus.NotFound => Results.NotFound(),
                    TemplateChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["請確認範本名稱。"] }),
                    _ => Results.Problem(statusCode: 409, title: "The preview could not be prepared; refresh and try again.")
                };
            }, allowApiCredential: false);
        }).Produces<PublicationTemplateUpdatePreview>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Preview a publication template update");
        admin.MapPut("/templates/{id:guid}", async (Guid id, ConfirmPublicationTemplateUpdate input, TemplateUpdateWorkflow workflow, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await workflow.ConfirmAsync(id, input, actor, token);
                return result.Status switch
                {
                    TemplateChangeStatus.Updated => Results.Ok(result.Template),
                    TemplateChangeStatus.NotFound => Results.NotFound(),
                    TemplateChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmation"] = ["預覽已失效，請重新預覽。"] }),
                    _ => Results.Problem(statusCode: 409, title: "The template or affected devices changed; refresh the preview.")
                };
            }, allowApiCredential: false);
        }).Produces<PublicationTemplateItem>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Confirm a publication template update");
        admin.MapPost("/templates/{id:guid}/retirement-preview", async (Guid id, PublicationTemplateRetirement input, TemplateRetirementWorkflow workflow, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await workflow.PreviewAsync(id, input, actor, token);
                return result.Status switch
                {
                    TemplateChangeStatus.Updated => Results.Ok(result.Preview),
                    TemplateChangeStatus.NotFound => Results.NotFound(),
                    TemplateChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["replacement"] = ["請確認替代預設範本。"] }),
                    _ => Results.Problem(statusCode: 409, title: "The preview could not be prepared; refresh and try again.")
                };
            }, allowApiCredential: false);
        }).Produces<PublicationTemplateRetirementPreview>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Preview retirement of a publication template");
        admin.MapPost("/templates/{id:guid}/retire", async (Guid id, ConfirmPublicationTemplateRetirement input, TemplateRetirementWorkflow workflow, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var status = await workflow.ConfirmAsync(id, input, actor, token);
                return status switch
                {
                    TemplateChangeStatus.Updated => Results.NoContent(),
                    TemplateChangeStatus.NotFound => Results.NotFound(),
                    TemplateChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmation"] = ["預覽已失效，請重新預覽。"] }),
                    _ => Results.Problem(statusCode: 409, title: "The template or affected devices changed; refresh the preview.")
                };
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Confirm retirement of a publication template");
        admin.MapGet("/templates/{id:guid}/impact", async (Guid id, int? page, TemplateReader templates, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (page is < 1 or > (TemplateImpact.MaximumDevices + TemplateImpact.PageSize - 1) / TemplateImpact.PageSize) return Results.BadRequest();
            var result = await templates.ImpactAsync(id, page ?? 1, token);
            if (result.Changed) return Results.Problem(statusCode: 409, title: "The template changed; refresh the preview.");
            if (result.TooMany) return Results.Problem(statusCode: 409, title: "Too many devices to preview safely.");
            return result.Impact is null ? Results.NotFound() : Results.Ok(result.Impact);
        }).Produces<PublicationImpact>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Preview devices assigned to a publication template");
        admin.MapGet("/{kind}/{id:guid}", async (string kind, Guid id, PublicationAdministration publications, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryKind(kind, out var resourceKind)) return Results.NotFound();
            return await publications.ReadAsync(resourceKind, id, token) is { } settings ? Results.Ok(settings) : Results.NotFound();
        }).Produces<PublicationSettings>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get publication settings");
        admin.MapGet("/devices/{id:guid}/history-preview", async (Guid id, DeviceHistoryPreviewReader preview, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await preview.ReadAsync(id, token) is { } history ? Results.Ok(history) : Results.NotFound();
        }).Produces<PublicDeviceHistory>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Preview eligible device latency history without publishing it");

        admin.MapGet("/{kind}/{id:guid}/preview", async (string kind, Guid id, PublicationPreviewReader preview, CancellationToken token) =>
        {
            if (!TryKind(kind, out var resourceKind)) return Results.NotFound();
            return await preview.ReadAsync(resourceKind, id, token) is { } result ? Results.Ok(result) : Results.NotFound();
        }).Produces<PublicationPreview>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Preview allowed publication status");
        admin.MapPut("/{kind}/{id:guid}", async (string kind, Guid id, PublicationChange change, PublicationAdministration publications, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryKind(kind, out var resourceKind)) return Results.NotFound();
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await publications.SetAsync(resourceKind, id, change, actor, token);
                return result.Status switch
                {
                    PublicationChangeStatus.Updated => Results.Ok(result.Settings),
                    PublicationChangeStatus.NotFound => Results.NotFound(),
                    PublicationChangeStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["publication"] = ["請確認公開名稱、政策及版本。名稱不可包含 IP 位址或過於複雜的符號組合。"] }),
                    _ => Results.Problem(statusCode: 409, title: "Publication settings changed or the resource is unavailable.")
                };
            }, allowApiCredential: false);
        }).Produces<PublicationSettings>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Change publication settings");

        var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous().RequireRateLimiting("api-client");
        publicApi.MapGet("/{kind}/{id:guid}/events", (string kind, Guid id, PublicReader reader, PublicStreamAdmission admission) =>
            TryKind(kind, out var resourceKind) ? (IResult)new PublicStatusStream(resourceKind, id, reader, admission) : Results.NotFound())
            .Produces(200, contentType: "text/event-stream").ProducesProblem(404).ProducesProblem(503)
            .WithSummary("Subscribe to a published resource's current status");
        publicApi.MapGet("/devices", async (string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.DevicesAsync(search?.Trim(), page ?? 1, null, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicDevice>>().ProducesProblem(400).WithSummary("List published Ping states");
        publicApi.MapGet("/devices/{id:guid}", async (Guid id, PublicReader reader, CancellationToken token) =>
            await reader.DeviceAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<PublicDevice>().ProducesProblem(404).WithSummary("Get a published Ping state");
        publicApi.MapGet("/devices/{id:guid}/history", async (Guid id, DateTimeOffset from, DateTimeOffset to,
            PublicDeviceHistoryReader reader, PublicHistoryAdmission admission, HttpContext context, CancellationToken token) =>
        {
            if (!PublicDeviceHistoryReader.Valid(from, to)) return Results.BadRequest();
            if (!await admission.Gate.WaitAsync(0, token))
            {
                context.Response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: 503, title: "Public history is busy; please retry.");
            }
            using var timeout = new CancellationTokenSource(admission.Timeout, admission.Clock);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            try { return await reader.ReadAsync(id, from, to, deadline.Token) is { } result ? Results.Ok(result) : Results.NotFound(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { context.Response.Headers.RetryAfter = "5"; return Results.Problem(statusCode: 503, title: "The public history request timed out; please retry."); }
            finally { admission.Gate.Release(); }
        }).Produces<PublicDeviceHistory>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(503)
            .WithSummary("Get explicitly published device latency history");
        publicApi.MapGet("/services", async (string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.ServicesAsync(search?.Trim(), page ?? 1, null, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicService>>().ProducesProblem(400).WithSummary("List published service health");
        publicApi.MapGet("/services/{id:guid}", async (Guid id, PublicReader reader, CancellationToken token) =>
            await reader.ServiceAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<PublicService>().ProducesProblem(404).WithSummary("Get a published service health state");
        publicApi.MapGet("/services/{id:guid}/history", async (Guid id, DateTimeOffset from, DateTimeOffset to,
            PublicServiceHistoryReader reader, PublicHistoryAdmission admission, HttpContext context, CancellationToken token) =>
        {
            if (!PublicServiceHistoryReader.Valid(from, to)) return Results.BadRequest();
            return await ReadServiceHistoryAsync(admission, context, token, ct => reader.ReadAsync(id, from, to, ct));
        }).Produces<PublicServiceHistory>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(422).ProducesProblem(503)
            .WithSummary("Get explicitly published daily service health history");
        publicApi.MapGet("/services/{id:guid}/history/periods", async (Guid id, DateTimeOffset from, DateTimeOffset to,
            PublicServiceHistoryReader reader, PublicHistoryAdmission admission, HttpContext context, CancellationToken token) =>
        {
            if (!PublicServiceHistoryReader.ValidPeriods(from, to)) return Results.BadRequest();
            return await ReadServiceHistoryAsync(admission, context, token, ct => reader.ReadPeriodsAsync(id, from, to, ct));
        }).Produces<PublicServiceHealthPeriods>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(422).ProducesProblem(503)
            .WithSummary("Get explicitly published service health periods within one day");
        publicApi.MapGet("/groups", async (string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.GroupsAsync(search?.Trim(), page ?? 1, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicGroup>>().ProducesProblem(400).WithSummary("List published groups and visible counts");
        publicApi.MapGet("/groups/{id:guid}", async (Guid id, PublicReader reader, CancellationToken token) =>
            await reader.GroupAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .Produces<PublicGroup>().ProducesProblem(404).WithSummary("Get a published group");
        publicApi.MapGet("/groups/{id:guid}/devices", async (Guid id, string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.DevicesAsync(search?.Trim(), page ?? 1, id, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicDevice>>().ProducesProblem(400).WithSummary("List published devices in a group");
        publicApi.MapGet("/groups/{id:guid}/services", async (Guid id, string? search, int? page, PublicReader reader, CancellationToken token) =>
            Valid(search, page) ? Results.Ok(await reader.ServicesAsync(search?.Trim(), page ?? 1, id, token)) : Results.BadRequest())
            .Produces<PublicPage<PublicService>>().ProducesProblem(400).WithSummary("List published services in a group");
    }

    private static async Task<IResult> ReadServiceHistoryAsync<T>(PublicHistoryAdmission admission, HttpContext context,
        CancellationToken token, Func<CancellationToken, Task<T?>> read) where T : class
    {
        if (!await admission.Gate.WaitAsync(0, token))
        {
            context.Response.Headers.RetryAfter = "5";
            return Results.Problem(statusCode: 503, title: "Public history is busy; please retry.");
        }
        using var timeout = new CancellationTokenSource(admission.Timeout, admission.Clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await read(deadline.Token) is { } result ? Results.Ok(result) : Results.NotFound(); }
        catch (HealthHistoryTooLargeException) { return Results.Problem(statusCode: 422, title: "The requested history is too large; choose a shorter range."); }
        catch (HealthHistoryBusyException) { context.Response.Headers.RetryAfter = "5"; return Results.Problem(statusCode: 503, title: "The service history changed; please retry."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { context.Response.Headers.RetryAfter = "5"; return Results.Problem(statusCode: 503, title: "The public history request timed out; please retry."); }
        finally { admission.Gate.Release(); }
    }

    private static bool Valid(string? search, int? page) => search?.Length is null or <= 256 && page is null or >= 1 and <= PublicReader.MaximumPage;

    internal static bool TryKind(string text, out PublicationResourceKind kind)
    {
        kind = text switch { "devices" => PublicationResourceKind.Device, "services" => PublicationResourceKind.Service, "groups" => PublicationResourceKind.Group, _ => 0 };
        return kind != 0;
    }
}
