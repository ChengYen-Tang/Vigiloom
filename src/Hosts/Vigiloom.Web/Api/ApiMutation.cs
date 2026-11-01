using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class ApiMutation
{
    public static async Task<IResult> ExecuteAsync(HttpContext context, Func<Guid, Task<IResult>> operation, bool allowApiCredential = true)
    {
        var apiIdentity = allowApiCredential ? context.User.Identities.FirstOrDefault(identity => identity.AuthenticationType == ApiCredentialHandler.SchemeName && identity.HasClaim("scope", "monitoring.write")) : null;
        if (apiIdentity is null)
        {
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        }
        var id = apiIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(id, out var actor) && actor != Guid.Empty ? await operation(actor) : Results.Forbid();
    }

}
