using System.Security.Claims;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Antiforgery;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Scheduling;

internal sealed class AdministratorDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => context.GetHttpContext().User.IsInRole(BuiltInRoles.Administrator);
}

internal sealed class DashboardGuard(RequestDelegate next, ILogger<DashboardGuard> logger)
{
    private static readonly HashSet<string> BatchActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "/jobs/enqueued/delete", "/jobs/enqueued/requeue", "/jobs/processing/delete", "/jobs/processing/requeue",
        "/jobs/scheduled/enqueue", "/jobs/scheduled/delete", "/jobs/succeeded/requeue", "/jobs/failed/requeue",
        "/jobs/failed/delete", "/jobs/deleted/requeue", "/jobs/awaiting/enqueue", "/jobs/awaiting/delete",
        "/recurring/remove", "/recurring/trigger"
    };

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery, DashboardAuditStore audit)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.User.Identity?.IsAuthenticated != true) { context.Response.StatusCode = 401; return; }
        if (!context.User.IsInRole(BuiltInRoles.Administrator)) { context.Response.StatusCode = 403; return; }
        var path = context.Request.Path.Value ?? "";
        // Match Hangfire's own route normalization before classifying a request as read-only.
        if (path.Length > 1) path = path.TrimEnd('/');
        var singleAction = path.StartsWith("/jobs/actions/delete/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/jobs/actions/requeue/", StringComparison.OrdinalIgnoreCase);
        if ((BatchActions.Contains(path) || singleAction) && !HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = 405;
            return;
        }
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) { await next(context); return; }
        if (!HttpMethods.IsPost(context.Request.Method) || !await antiforgery.IsRequestValidAsync(context))
        {
            context.Response.StatusCode = 403;
            return;
        }
        // Hangfire refreshes its read-only statistics through an antiforgery-protected POST.
        if (string.Equals(path, "/stats", StringComparison.OrdinalIgnoreCase)) { await next(context); return; }
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
        {
            context.Response.StatusCode = 403;
            return;
        }

        string action;
        string[] ids;
        if (BatchActions.Contains(path) && context.Request.HasFormContentType)
        {
            action = path;
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            ids = form["jobs[]"].Select(value => value ?? "").Distinct(StringComparer.Ordinal).ToArray();
        }
        else if (singleAction)
        {
            var lastSlash = path.LastIndexOf('/');
            action = path[..lastSlash];
            ids = [path[(lastSlash + 1)..]];
        }
        else { context.Response.StatusCode = 405; return; }
        if (ids.Length is 0 or > 100 || ids.Any(id => id.Length is 0 or > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or ':' or '.'))))
        {
            context.Response.StatusCode = 400;
            return;
        }

        long[] auditIds;
        try { auditIds = await audit.BeginAsync(actor, action, ids, context.RequestAborted); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Dashboard audit could not be saved: {ExceptionType}", exception.GetType().Name);
            context.Response.StatusCode = 503;
            return;
        }
        var result = "unknown";
        try
        {
            await next(context);
            result = context.Response.StatusCode is >= 200 and < 300 ? "accepted" : "rejected";
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await audit.CompleteAsync(auditIds, result, timeout.Token); }
            catch (Exception exception) { logger.LogError("Dashboard audit outcome could not be saved: {ExceptionType}", exception.GetType().Name); }
        }
    }
}
