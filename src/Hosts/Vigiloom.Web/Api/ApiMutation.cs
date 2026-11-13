using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class ApiMutation
{
    public static async Task<IResult> ExecuteAsync(HttpContext context, Func<Guid, Task<IResult>> operation, bool allowApiCredential = true)
    {
        var apiIdentity = allowApiCredential ? context.User.Identities.FirstOrDefault(identity => identity.AuthenticationType == ApiCredentialHandler.SchemeName && identity.HasClaim("scope", "monitoring.write")) : null;
        ClaimsPrincipal? cookiePrincipal = null;
        if (apiIdentity is null)
        {
            var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!cookie.Succeeded || cookie.Principal is null) return Results.Forbid();
            cookiePrincipal = cookie.Principal;
            var original = context.User;
            context.User = cookiePrincipal;
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            finally { context.User = original; }
        }
        var id = apiIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? cookiePrincipal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(id, out var actor) && actor != Guid.Empty ? await operation(actor) : Results.Forbid();
    }

}
