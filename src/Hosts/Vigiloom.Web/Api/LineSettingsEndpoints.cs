using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class LineSettingsEndpoints
{
    public static void MapLineSettingsEndpoints(this WebApplication app)
    {
        app.MapPost("/settings/line/group", async (HttpContext context, Vigiloom.Web.Operations.LineManagement channel, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var form = await context.Request.ReadFormAsync(token);
                if (!Guid.TryParse(form["groupId"], out var id) || !bool.TryParse(form["approved"], out var approved)) return Results.BadRequest();
                return await channel.SetGroupAsync(id, form["name"].ToString(), approved, actor, token)
                    ? Results.Redirect("/settings/line?saved=group") : Results.BadRequest();
            }, allowApiCredential: false)).RequireAuthorization(AccessPolicies.Administrator);
        app.MapPost("/settings/line/test", async (HttpContext context, Vigiloom.Web.Operations.LineManagement queue, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ =>
            {
                var form = await context.Request.ReadFormAsync(token);
                if (!Guid.TryParse(form["groupId"], out var id)) return Results.BadRequest();
                return await queue.EnqueueTestAsync(id, token) is { } delivery
                    ? Results.Redirect($"/settings/line?test={delivery:D}") : Results.BadRequest();
            }, allowApiCredential: false)).RequireAuthorization(AccessPolicies.Administrator);
    }

}
